"""Broker-independent, tested strategy / risk / state machine.

This module never sends orders. It returns immutable trade intents; caller must persist
intent BEFORE sending and wait for authoritative broker fill reconciliation.
"""
from __future__ import annotations
from dataclasses import dataclass, asdict, field
from datetime import datetime, time, date
from pathlib import Path
from typing import Optional
import json, os, uuid, math


@dataclass
class Settings:
    max_positions: int = 3
    position_fraction: float = 0.20
    daily_loss_limit_pct: float = 0.03
    stop_pct: float = 0.06
    take_pct: float = 0.12
    trail_pct: float = 0.07
    cash_reserve_pct: float = 0.10
    min_trade_value: int = 3_000_000_000  # 20-day average: KRW
    max_daily_orders: int = 6
    min_stock_price: int = 2000
    max_stock_price: int = 300000
    max_slippage_pct: float = 0.015
    order_buffer: float = 0.03
    scan_interval_seconds: float = 2.0
    quote_max_age_seconds: int = 25
    # The absolute investment cap is a runtime setting, not compiled principal.
    # 0 means unlimited, but position fraction, cash reserve, etc still apply.
    max_order_krw: int = 100_000

    def validate(self):
        assert 1 <= self.max_positions <= 20
        assert 0 < self.position_fraction <= 0.5
        assert 0 < self.stop_pct < 0.5 and 0 < self.take_pct < 1
        assert 0 < self.trail_pct < 0.5
        assert 0 <= self.cash_reserve_pct < 0.9
        assert 1 <= self.max_daily_orders <= 100
        assert self.max_order_krw >= 0


@dataclass
class Bar:
    day: str  # YYYYMMDD
    high: int
    low: int
    close: int
    volume: int


@dataclass
class Symbol:
    code: str
    name: str
    market: str


@dataclass
class Position:
    code: str
    name: str
    qty: int
    avg_price: int
    high_since_entry: int
    bought_day: str
    source: str = 'BOT'


@dataclass
class Holding:
    code: str
    name: str
    qty: int
    avg_price: int
    pending_buy: int = 0
    pending_sell: int = 0
    credit_qty: int = 0


@dataclass
class Quote:
    code: str
    price: int
    received_at: datetime
    regular_session: bool
    exchange_time: str = ''


@dataclass
class Candidate:
    code: str
    name: str
    score: float
    breakout: int
    avg_value: int
    avg5: float
    avg20: float
    yclose: int
    bars_day: str


@dataclass
class Intent:
    intent_id: str
    side: str
    code: str
    name: str
    qty: int
    reference_price: int
    before_qty: int
    created: str
    state: str = 'UNKNOWN'
    order_number: str = ''
    message: str = ''


def normalize_code(raw: str) -> str:
    code = str(raw).strip().lstrip('A').strip()
    return code if len(code) == 6 and code.isdigit() else ''


def market_window(now: datetime, side: str) -> bool:
    if now.weekday() >= 5:
        return False
    # Real regular-session tick is REQUIRED in addition to this conservative clock gate.
    if side == 'BUY':
        return time(9, 25) <= now.time() < time(14, 40)
    return time(9, 5) <= now.time() < time(15, 10)


def make_candidate(sym: Symbol, bars: list[Bar], cfg: Settings, today: str) -> Optional[Candidate]:
    # Only fully completed trading sessions; exclude partial today's bar.
    arr = sorted({b.day: b for b in bars if b.day < today and b.close > 0 and b.high > 0
                  and b.low > 0 and b.volume > 0}.values(), key=lambda b: b.day)
    if len(arr) < 40: return None
    recent = arr[-40:]
    if any(x.high < x.low or x.high < x.close or x.low > x.close for x in recent): return None
    last = recent[-1]
    if not (cfg.min_stock_price <= last.close <= cfg.max_stock_price): return None
    avg5 = sum(x.close for x in recent[-5:]) / 5
    avg20 = sum(x.close for x in recent[-20:]) / 20
    prev20 = sum(x.close for x in recent[-21:-1]) / 20
    avgvalue = int(sum(x.close * x.volume for x in recent[-20:]) / 20)
    if not (avg5 > avg20 >= prev20 and avgvalue >= cfg.min_trade_value): return None
    if last.close / recent[-21].close > 1.50: return None  # cap overly extended names
    breakout = max(x.high for x in recent[-20:])
    score = (avg5 / avg20 - 1) * 100 + min(30.0, avgvalue / 1e10)
    return Candidate(sym.code, sym.name, score, breakout, avgvalue,
                     avg5, avg20, last.close, last.day)


def buy_trigger(c: Candidate, q: Quote, now: datetime, cfg: Settings) -> bool:
    if q.code != c.code or not q.regular_session or not market_window(now, 'BUY'):
        return False
    if (now-q.received_at).total_seconds() > cfg.quote_max_age_seconds: return False
    if q.received_at > now or q.price <= 0: return False
    return (q.price > c.breakout and q.price <= c.breakout * 1.025 and
            q.price <= c.yclose * 1.095)


def sell_reason(p: Position, q: Quote, now: datetime, cfg: Settings) -> Optional[str]:
    if q.code != p.code or not q.regular_session or not market_window(now, 'SELL'):
        return None
    if q.price <= 0 or (now-q.received_at).total_seconds()>cfg.quote_max_age_seconds or q.received_at>now:
        return None
    base = p.avg_price
    if base <= 0 or p.qty <= 0: return None
    if q.price <= base * (1-cfg.stop_pct): return '손절'
    if q.price >= base * (1+cfg.take_pct): return '익절'
    if q.price <= p.high_since_entry * (1-cfg.trail_pct) and p.high_since_entry >= base * 1.03:
        return '추적손절'
    return None


def order_quantity(price: int, verified_cash: Optional[int], equity: int,
                   cfg: Settings, positions_count: int, daily_buy_spent: int) -> int:
    # None is not zero: it means broker did not verify available cash. Always block.
    if verified_cash is None or verified_cash <= 0 or price <= 0:
        return 0
    if positions_count >= cfg.max_positions or equity <= 0: return 0
    remaining_cash = max(0, verified_cash - int(equity*cfg.cash_reserve_pct))
    budget = min(int(equity * cfg.position_fraction), remaining_cash)
    if cfg.max_order_krw: budget = min(budget, cfg.max_order_krw)
    return max(0, int(budget // (price * (1 + cfg.order_buffer))))


def quote_fresh(q: Quote, now: datetime, cfg: Settings) -> bool:
    return q.regular_session and q.price > 0 and 0 <= (now-q.received_at).total_seconds() <= cfg.quote_max_age_seconds


class Ledger:
    """Crash-safe append-only JSONL journal; no silent clearing of unresolved orders."""
    def __init__(self, directory: Path):
        directory.mkdir(parents=True, exist_ok=True)
        self.directory = directory
        self.logpath = directory / 'journal.jsonl'
        self.positions_path = directory / 'managed_positions.json'
        self.entries = self._read_log()
        self.positions: dict[str, Position] = self._read_positions()
        self._recover_positions()

    def _read_log(self):
        if not self.logpath.exists(): return []
        result = []
        for line in self.logpath.read_text(encoding='utf-8').splitlines():
            if line.strip(): result.append(json.loads(line))
        return result

    def _read_positions(self):
        if not self.positions_path.exists(): return {}
        raw = json.loads(self.positions_path.read_text(encoding='utf-8'))
        return {k: Position(**v) for k,v in raw.items()}

    def _recover_positions(self):
        """Rebuild filled holdings from append-only journal after a crash.

        Persistent highs are overlaid only when the reconstructed position
        matches the last saved snapshot. Never infer fills from order requests.
        """
        snap = self.positions
        rebuilt = {}
        intents = {row['id']:row for row in self.entries if row.get('type')=='ORDER_INTENT'}
        for row in self.entries:
            if row.get('type')!='ORDER_FINAL' or int(row.get('filled_qty',0))<=0:
                continue
            original = intents.get(row['id'])
            if original is None:
                raise RuntimeError('원본 주문기록 없는 체결기록')
            code=original['code'];count=int(row['filled_qty']);px=int(row.get('avg_price',0))
            if original['side']=='BUY':
                old=rebuilt.get(code)
                if old:
                    avg=round((old.qty*old.avg_price+count*px)/(old.qty+count))
                    old.qty+=count;old.avg_price=avg
                else:
                    rebuilt[code]=Position(code,original.get('name',code),count,px,px,
                                           row.get('time','')[:10])
            elif original['side']=='SELL':
                old=rebuilt.get(code)
                if not old or old.qty<count:
                    raise RuntimeError('매도 체결로그와 로컬 보유수량 불일치')
                old.qty-=count
                if old.qty==0:del rebuilt[code]
        for code,p in rebuilt.items():
            if code in snap and snap[code].qty==p.qty and snap[code].avg_price==p.avg_price:
                p.high_since_entry=max(p.high_since_entry,snap[code].high_since_entry)
        self.positions=rebuilt
        self.persist_positions()

    def record(self, type_: str, **kwargs):
        row = dict(type=type_, time=datetime.now().isoformat(), **kwargs)
        with self.logpath.open('a',encoding='utf-8') as f:
            f.write(json.dumps(row,ensure_ascii=False)+'\n');f.flush();os.fsync(f.fileno())
        self.entries.append(row)
        return row

    def persist_positions(self):
        path = self.positions_path.with_suffix('.tmp')
        with path.open('w',encoding='utf-8') as f:
            json.dump({k:asdict(p) for k,p in self.positions.items()},f,ensure_ascii=False,indent=2)
            f.flush();os.fsync(f.fileno())
        os.replace(path,self.positions_path)

    def unresolved(self):
        # Persistence of the order intent and all subsequent state transitions.
        latest = {}
        for r in self.entries:
            if r.get('type') == 'ORDER_INTENT': latest[r['id']] = 'UNKNOWN'
            if r.get('type') == 'ORDER_FINAL': latest[r['id']] = r['status']
        return [k for k,v in latest.items() if v == 'UNKNOWN']

    def daily_order_count(self, today: str):
        return sum(1 for r in self.entries if r.get('type')=='ORDER_INTENT' and
                   r.get('time','')[:10]==today)

    def daily_buy_committed(self,today: str):
        return sum(int(r.get('reference_price',0))*int(r.get('qty',0)) for r in self.entries
                   if r.get('type')=='ORDER_INTENT' and r.get('side')=='BUY' and
                   r.get('time','')[:10]==today)

    def new_intent(self, side: str, code: str, name: str, qty: int, price: int,
                   before_qty: int) -> Intent:
        if self.unresolved(): raise RuntimeError('이전 주문 미확정: 발주 금지')
        if qty <= 0 or price <= 0: raise ValueError('주문수량 및 기준가격 오류')
        uid = str(uuid.uuid4())
        self.record('ORDER_INTENT',id=uid,side=side,code=code,name=name,qty=qty,
                    reference_price=price,before_qty=before_qty)
        return Intent(uid,side,code,name,qty,price,before_qty,datetime.now().isoformat())

    def final(self, intent: Intent, status: str, filled_qty: int, avg_price: int, order_number=''):
        if status not in ('FILLED','PARTIAL_TERMINAL','REJECTED_VERIFIED'):
            raise ValueError('Unverified status must remain unresolved')
        if filled_qty < 0 or filled_qty > intent.qty: raise ValueError('체결수량 오류')
        self.record('ORDER_FINAL',id=intent.intent_id,status=status,filled_qty=filled_qty,
                    avg_price=avg_price,order_number=order_number)
        if filled_qty:
            self._recover_positions()


def reconcile_holdings(intent: Intent, holdings: dict[str,Holding]) -> tuple[str,int,int]:
    """Conservative finalization: require broker-confirmed no pending qty.
    This is deliberately not a complete order/fill-by-order correlation system.
    External manual trades in the same symbol MUST be forbidden while running.
    """
    b=holdings.get(intent.code)
    if b and (b.pending_buy or b.pending_sell): return 'WAIT',0,0
    now_qty=b.qty if b else 0
    delta = now_qty - intent.before_qty if intent.side=='BUY' else intent.before_qty - now_qty
    if 0 < delta <= intent.qty:
        if intent.side=='BUY':
            if b is None or b.avg_price <= 0: return 'CONFLICT',0,0
            if delta==intent.qty:return 'FILLED',delta,b.avg_price
            return 'PARTIAL_TERMINAL',delta,b.avg_price
        # Sell execution price cannot be inferred from remaining holding avg; need fill statement.
        return 'SELL_NEEDS_EXECUTION_PRICE',delta,0
    if delta == 0: return 'WAIT',0,0
    return 'CONFLICT',0,0
