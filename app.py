"""Shinhan AutoTrader v0.3 (paper demo + experimental live ActiveX GUI).

Safety: live orders gated and crash-safe. Broker integration NOT validated with
an actual 2026 Shinhan installation. See README before connecting a real account.
"""
import os, sys, random, traceback, hashlib
from pathlib import Path
from datetime import datetime, timedelta, time
from PyQt5.QtCore import QTimer, Qt
from PyQt5.QtWidgets import (QApplication,QWidget,QVBoxLayout,QHBoxLayout,QFormLayout,
  QLabel,QLineEdit,QPushButton,QPlainTextEdit,QComboBox,QSpinBox,QDoubleSpinBox,
  QCheckBox,QMessageBox,QTableWidget,QTableWidgetItem,QTabWidget,QGroupBox)
from engine import (Settings,Ledger,Symbol,Bar,Quote,Holding,Position,Intent,
  make_candidate,buy_trigger,sell_reason,order_quantity,market_window,
  reconcile_holdings,quote_fresh)

DATA=Path(os.getenv('LOCALAPPDATA',str(Path.home()))) / 'ShinhanAutoTrader_v03'
DATA.mkdir(parents=True,exist_ok=True)


def fmt(v):return f'{v:,}'


class Window(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle('신한 자동매매 v0.3 — 종목탐색 / 자동매수 / 자동매도')
        self.resize(1030,790)
        self.cfg=Settings(); self.cfg.validate()
        self.mode='DEMO';self.broker=None
        self.ledger=Ledger(DATA/'paper')
        self.armed=False;self.buy_allowed=True;self.cash_proof=None
        self.sim_cash=self.paper_cash()
        self.symbols=[];self.cursor=0;self.candidates={};self.quotes={};self.holdings={}
        self.pending_intent=None;self.pending_ack=False;self.pending_since=None
        self.last_account_sync=None;self.last_quote_check=None
        self.daily_start_equity=None;self.scanned=0;self.last_symbol=''
        self.needs_reconcile=False;self.current_query=''
        self.last_broker_poll=datetime.min
        self.scanning=True;self.last_probe_cursor=-1;self.position_cursor=0
        self._build_ui()
        self.timer=QTimer(self);self.timer.timeout.connect(self.tick);self.timer.start(2200)
        self.log('모의모드 준비. 실제 주문은 명시적으로 전환/확인하기 전까지 차단됩니다.')
        self.log('이 프로그램은 수익률이 검증된 매매전략이 아닙니다.')

    def paper_cash(self):
        """Demo paper account survives application restarts."""
        intents={r['id']:r for r in self.ledger.entries if r.get('type')=='ORDER_INTENT'}
        cash=2_000_000
        for r in self.ledger.entries:
            if r.get('type')!='ORDER_FINAL':continue
            original=intents.get(r['id'])
            if not original:continue
            value=int(r.get('filled_qty',0))*int(r.get('avg_price',0))
            cash+=value if original['side']=='SELL' else -value
        return cash

    def _build_ui(self):
        root=QVBoxLayout(self)
        title=QLabel('신한 자동매매 | 코스피·코스닥 종목 탐색, 자동 매수·매도')
        title.setStyleSheet('font-size:19px;font-weight:700;padding:7px 0')
        root.addWidget(title)
        info=QLabel('⚠ 실계좌 기능은 신한i indi 공식 사양 대조/PC 테스트가 끝나지 않은 실험용입니다. '
                    '모의모드에서 엔진을 먼저 점검하십시오.')
        info.setWordWrap(True);info.setStyleSheet('color:#a64723')
        root.addWidget(info)
        upper=QHBoxLayout();root.addLayout(upper)
        conn=QGroupBox('증권사 / 계좌');left=QFormLayout(conn);upper.addWidget(conn,1)
        self.mode_box=QComboBox();self.mode_box.addItems(['모의매매(가상)','신한 실계좌(실주문 위험)'])
        self.mode_box.currentIndexChanged.connect(self.change_mode)
        self.account=QLineEdit();self.account.setPlaceholderText('계좌번호 - 로컬 입력, 저장하지 않음')
        self.password=QLineEdit();self.password.setEchoMode(QLineEdit.Password)
        self.password.setPlaceholderText('비밀번호 - 저장하지 않음')
        self.cash=QLineEdit();self.cash.setPlaceholderText('HTS에서 직접 확인한 현금100% 주문가능액')
        self.cash.setText('')
        self.cash.setToolTip('미확인/빈칸이면 실계좌 신규매수는 차단됩니다. 원금 고정 설정이 아닙니다.')
        for text,w in [('운용 모드',self.mode_box),('계좌번호',self.account),('계좌비밀번호',self.password),
                       ('주문가능액 확인',self.cash)]:left.addRow(text,w)
        self.verify_cash=QPushButton('HTS 현금100% 주문가능액 재확인 표시')
        self.verify_cash.clicked.connect(self.confirm_cash)
        left.addRow(self.verify_cash)
        risk=QGroupBox('실시간 조정 가능한 매매 설정');rf=QFormLayout(risk);upper.addWidget(risk,1)
        self.frac=QSpinBox();self.frac.setRange(1,50);self.frac.setValue(20);self.frac.setSuffix(' % / 종목')
        self.max_pos=QSpinBox();self.max_pos.setRange(1,20);self.max_pos.setValue(3)
        self.stop=QSpinBox();self.stop.setRange(1,30);self.stop.setValue(6);self.stop.setSuffix(' %')
        self.take=QSpinBox();self.take.setRange(1,50);self.take.setValue(12);self.take.setSuffix(' %')
        self.trail=QSpinBox();self.trail.setRange(1,30);self.trail.setValue(7);self.trail.setSuffix(' %')
        self.daily_loss=QSpinBox();self.daily_loss.setRange(1,20);self.daily_loss.setValue(3);self.daily_loss.setSuffix(' %')
        self.cap=QSpinBox();self.cap.setRange(0,2_000_000_000);self.cap.setSingleStep(50_000)
        self.cap.setValue(100_000);self.cap.setSuffix(' 원 (0=별도 상한 없음)')
        for text,w in [('종목당 투자비율',self.frac),('최대 보유종목',self.max_pos),('손절',self.stop),
                       ('익절',self.take),('추적손절',self.trail),('일일 평가손실 차단',self.daily_loss),
                       ('1회 금액 상한',self.cap)]:rf.addRow(text,w)
        buttons=QHBoxLayout();root.addLayout(buttons)
        self.connect=QPushButton('신한i indi 연결 / 종목조회');self.connect.clicked.connect(self.connect_ind)
        self.start=QPushButton('자동매매 시작');self.start.clicked.connect(self.arm)
        self.pause=QPushButton('매수 중지 (매도 감시 유지)');self.pause.clicked.connect(self.pause_buys)
        self.halt=QPushButton('전체 자동주문 긴급중지');self.halt.clicked.connect(self.halt_all)
        for b in (self.connect,self.start,self.pause,self.halt):buttons.addWidget(b)
        self.status=QLabel('상태: 대기')
        self.status.setStyleSheet('padding:8px;background:#eeeeee;color:#202020;font-weight:bold')
        root.addWidget(self.status)
        self.tabs=QTabWidget();root.addWidget(self.tabs,1)
        self.scan_table=QTableWidget(0,5)
        self.scan_table.setHorizontalHeaderLabels(['종목코드','종목명','추세 점수','돌파기준가','20일 평균 거래대금'])
        self.scan_table.horizontalHeader().setStretchLastSection(True)
        self.tabs.addTab(self.scan_table,'발굴 후보')
        self.pos_table=QTableWidget(0,6)
        self.pos_table.setHorizontalHeaderLabels(['종목코드','종목명','보유수량','평균단가','최근 시세','평가수익률'])
        self.pos_table.horizontalHeader().setStretchLastSection(True)
        self.tabs.addTab(self.pos_table,'자동매매 보유분')
        self.log_box=QPlainTextEdit();self.log_box.setReadOnly(True)
        self.tabs.addTab(self.log_box,'실행·주문 기록')
        self.tabs.setCurrentWidget(self.log_box)
        footer=QLabel('거래기록: '+str(DATA)+' | 프로그램 종료·인터넷 단절 시 손절 자동실행 불가 | 실계좌 잔고와 HTS를 함께 확인')
        footer.setWordWrap(True);root.addWidget(footer)

    def log(self,msg):
        stamp=datetime.now().strftime('%H:%M:%S')
        self.log_box.appendPlainText(stamp+'  '+str(msg))
        # Log non-secret messages, but never account/password/whole broker exception objects.
        try:
            p=DATA/'app_events.log'
            with p.open('a',encoding='utf-8') as f:f.write(stamp+'  '+str(msg)+'\n')
        except OSError:pass

    def now(self):
        if self.mode=='DEMO':
            return datetime.combine(datetime.now().date(),time(10,30))
        return datetime.now()

    def set_status(self,msg):
        self.status.setText(f'상태: {msg} | 스캔 {self.scanned}/{len(self.symbols)} | 후보 {len(self.candidates)}'
                           f' | 로컬 관리 {len(self.ledger.positions)}종목')

    def settings(self):
        self.cfg.position_fraction=self.frac.value()/100
        self.cfg.max_positions=self.max_pos.value()
        self.cfg.stop_pct=self.stop.value()/100
        self.cfg.take_pct=self.take.value()/100
        self.cfg.trail_pct=self.trail.value()/100
        self.cfg.daily_loss_limit_pct=self.daily_loss.value()/100
        self.cfg.max_order_krw=self.cap.value()
        self.cfg.validate()

    def change_mode(self,index):
        if self.armed or self.pending_intent:
            self.mode_box.blockSignals(True)
            self.mode_box.setCurrentIndex(0 if self.mode=='DEMO' else 1)
            self.mode_box.blockSignals(False)
            self.log('실행 중/주문 미확정 상태에서는 모드를 바꿀 수 없습니다.')
            return
        self.mode='DEMO' if index==0 else 'LIVE'
        self.ledger=Ledger(DATA/('paper' if self.mode=='DEMO' else 'live'))
        self.broker=None;self.cash_proof=None;self.armed=False;self.buy_allowed=True
        if self.mode=='DEMO': self.sim_cash=self.paper_cash()
        self.symbols=[];self.cursor=0;self.candidates={};self.quotes={};self.holdings={}
        self.scanned=0;self.scanning=True;self.last_probe_cursor=-1
        self.log('운용모드 변경: '+self.mode+' / 주문상태 초기화(미확정 로그는 유지)')
        self.set_status('모드 변경; 연결 필요')

    def demo_data(self):
        rng=random.Random(73011)
        self.symbols=[Symbol(f'{100000+i:06d}',f'가상연습종목{i:03d}',str(i%2)) for i in range(1,101)]
        self.demo_bars={}
        start=datetime(2026,8,1)
        for i,s in enumerate(self.symbols):
            base=9000+i*80; arr=[]
            for d in range(60):
                close=base + max(0,d-25)*(45+(i%7)*9) + int(rng.uniform(-30,30))
                arr.append(Bar((start+timedelta(days=d)).strftime('%Y%m%d'),
                               int(close*1.006),int(close*.994),close,350000+i*15000))
            self.demo_bars[s.code]=arr
        self.scanned=0;self.cursor=0;self.candidates={}
        for sym in self.symbols:
            self.process_bars(sym.code,self.demo_bars[sym.code])
        self.cursor=len(self.symbols)
        self.log('가상 종목 100개 생성(실제 주가 아님). 자동 종목발굴/매도 흐름 검증용')

    def connect_ind(self):
        if self.mode=='DEMO':
            self.demo_data();self.set_status('가상시장 종목조회 완료');return
        if sys.platform!='win32':
            self.log('실계좌는 Windows에서만 연결할 수 있습니다.');return
        try:
            from indi_client import IndiClient
            self.broker=IndiClient(self)
            self.broker.got.connect(self.on_data)
            self.broker.failed.connect(self.on_error)
            self.broker.event.connect(self.on_event)
            if not self.broker.load_stocks():raise RuntimeError('종목 조회 시작 실패')
            self.log('신한 OCX 연결 / 전체 국내주식 조회 시작. 실계좌 주문은 아직 차단 상태')
        except Exception as exc:
            self.broker=None
            self.log('연결 실패: '+type(exc).__name__+': '+str(exc))

    def confirm_cash(self):
        if self.mode=='DEMO':self.log('가상모드 현금은 자동 관리됩니다.');return
        try:
            amount=int(self.cash.text().replace(',','').strip())
            if amount<0:raise ValueError
        except ValueError:
            self.cash_proof=None;self.log('현금매수가능액 확인 실패 — 신규 매수 차단');return
        self.cash_proof={'amount':amount,'at':datetime.now(),'used':0}
        self.log('HTS에서 직접 확인한 현금100% 주문가능액 '+fmt(amount)+'원 입력됨. 자동조회된 값이 아닙니다.')

    def arm(self):
        self.settings()
        if self.mode=='LIVE':
            a=self.account.text().strip()
            if not(a.isdigit() and 8<=len(a)<=11):self.log('계좌번호 형식을 확인하십시오.');return
            token=hashlib.sha256(a.encode('utf-8')).hexdigest()[:16]
            self.ledger=Ledger(DATA/('live_'+token))
        if self.ledger.unresolved():
            self.log('실행 차단: 이전 주문 미확정 '+','.join(self.ledger.unresolved())[:100]);return
        if self.armed:self.log('이미 자동매매 실행 중');return
        if self.mode=='DEMO':
            if not self.symbols:self.demo_data()
            self.armed=True;self.buy_allowed=True
            self.log('가상 자동매매 시작 — 실제 증권사 주문 없음')
            return
        if self.broker is None or not self.symbols:
            self.log('신한 연결/전체 종목 조회가 완료돼야 합니다.');return
        if not self.account.text().isdigit() or not (8<=len(self.account.text())<=11):
            self.log('계좌번호 형식 확인');return
        if not self.password.text():self.log('계좌비밀번호 필요');return
        if self.cash_proof is None:
            self.log('신한 주문가능액 조회 TR 미확인 → 수동으로 확인한 HTS 현금100% 매수가능액 입력 필요')
            return
        if (datetime.now()-self.cash_proof['at']).total_seconds()>300:
            self.log('주문가능액 확인 5분 초과 → HTS에서 다시 확인해야 합니다.');return
        if not market_window(self.now(),'BUY'):
            self.log('정규장 보수적 매매허용 시각이 아닙니다.');return
        answer=QMessageBox.warning(self,'실제 현금 주문 활성화',
            '실제 계좌에 매수·매도 주문이 전송될 수 있습니다.\n'
            '신한i indi 주문/체결/잔고 사양은 이 PC에서 검증되지 않았습니다.\n'
            '계좌에 기존 보유한 주식은 자동 매도 대상에서 제외합니다.\n'
            '연결 끊기면 모든 자동주문을 중지합니다.\n\n'
            '실주문을 실행하시겠습니까?',QMessageBox.Yes|QMessageBox.No,QMessageBox.No)
        if answer!=QMessageBox.Yes:return
        try:
            self.broker.subscribe_account(self.account.text().strip())
        except Exception as exc:
            self.log('실계좌 실행 차단: 주문/체결 실시간 이벤트 미등록: '+str(exc));return
        self.armed=True;self.buy_allowed=True
        self.account.setReadOnly(True)
        self.needs_reconcile=True
        self.log('실계좌 자동매매 활성화 — 바로 계좌 잔고 확인, 주문가능액은 수동 확인값')

    def halt_all(self):
        self.armed=False
        self.buy_allowed=False
        self.account.setReadOnly(False)
        self.log('긴급 중지: 신규 매수·매도 주문 차단. 기존 접수 주문 취소 아님; HTS 확인 필요')
        self.set_status('자동주문 중지')

    def pause_buys(self):
        self.buy_allowed=False
        self.log('신규 매수만 중단: 자동 매도 감시는 계속')

    def on_error(self,msg):
        self.log('브로커 오류: '+str(msg));self.halt_all()

    def on_event(self,kind):
        self.needs_reconcile=True
        self.log('계좌 체결/잔고 이벤트 수신: '+str(kind)+' (조회 후 확인)')

    def equity(self):
        if self.mode=='DEMO':
            return self.sim_cash+sum(p.qty*(self.quotes[p.code].price if p.code in self.quotes else p.avg_price)
                              for p in self.ledger.positions.values())
        cash=(self.cash_proof['amount']-self.cash_proof['used']) if self.cash_proof else 0
        return max(0,cash)+sum(h.qty*h.avg_price for h in self.holdings.values() if h.qty>0)

    def available_cash(self):
        if self.mode=='DEMO':return self.sim_cash
        # Cash is not broker-verified in the current codebase. Extra live safety:
        # manually reverify HTS purchasing power after *each* order and within 5m.
        if self.cash_proof is None or (datetime.now()-self.cash_proof['at']).total_seconds()>300:
            return None
        return max(0,self.cash_proof['amount']-self.cash_proof['used'])

    def tick(self):
        try:self._tick()
        except Exception as exc:
            self.log('안전정지 '+type(exc).__name__+': '+str(exc));self.halt_all()

    def _tick(self):
        if not self.armed:return
        self.settings()
        if self.mode=='LIVE' and self.broker is None:self.halt_all();return
        if self.mode=='LIVE' and self.broker.busy():return
        if self.ledger.unresolved() and self.pending_intent is None:
            self.log('기존 미확정 주문 감지 — HTS 확인 필요');self.halt_all();return
        if self.pending_intent:
            if self.mode=='DEMO':return
            if (datetime.now()-self.last_broker_poll).total_seconds()>=12:
                self.last_broker_poll=datetime.now()
                self.broker.load_holdings(self.account.text(),self.password.text())
            return
        if self.mode=='LIVE' and (self.needs_reconcile or self.last_account_sync is None or
            (datetime.now()-self.last_account_sync).total_seconds()>90):
            self.needs_reconcile=False
            self.broker.load_holdings(self.account.text(),self.password.text())
            return
        if self.mode=='LIVE' and self.last_account_sync and (datetime.now()-self.last_account_sync).total_seconds()>120:
            self.log('계좌 잔고 최신성 120초 초과');self.halt_all();return
        if self.daily_start_equity is None:self.daily_start_equity=self.equity()
        # Check own positions before slow stock-universe scanning.
        if self.ledger.positions:
            if self.last_quote_check is None or (datetime.now()-self.last_quote_check).total_seconds()>8:
                self.last_quote_check=datetime.now()
                pool=list(self.ledger.positions)
                code=pool[self.position_cursor%len(pool)]
                self.position_cursor+=1
                self.fetch_quote(code)
                return
        # Spread full-market scan over the session; throttle to one historical TR per tick.
        if (self.cursor < len(self.symbols) and self.cursor > 0 and
            self.cursor % 12 == 0 and self.last_probe_cursor != self.cursor and
            self.candidates and self.buy_allowed):
            self.last_probe_cursor=self.cursor
            top=sorted(self.candidates.values(),key=lambda a:a.score,reverse=True)
            self.fetch_quote(top[0].code)
            return
        if self.cursor<len(self.symbols):
            s=self.symbols[self.cursor];self.cursor+=1
            self.last_symbol=s.code
            if self.mode=='DEMO':
                self.process_bars(s.code,self.demo_bars[s.code]);return
            if self.broker.load_bars(s.code):return
        # Once candidates available start probing live prices.
        available=[c for c in sorted(self.candidates.values(),key=lambda a:a.score,reverse=True)
                   if c.code not in self.ledger.positions]
        if available:
            c=available[(int(datetime.now().timestamp())//4)%min(len(available),10)]
            self.fetch_quote(c.code)
        self.set_status('종목 탐색 및 자동매매 감시')

    def fetch_quote(self,code):
        if self.mode=='DEMO':
            base=self.candidates[code].breakout if code in self.candidates else self.ledger.positions[code].avg_price
            if code in self.ledger.positions:
                price=int(self.ledger.positions[code].avg_price*1.125)
            else:price=int(base*1.010)
            self.process_quote(Quote(code,price,self.now(),True,'103000'))
        else:self.broker.load_quote(code)

    def process_bars(self,code,bars):
        s=next((x for x in self.symbols if x.code==code),None)
        if s:
            c=make_candidate(s,bars,self.cfg,self.now().strftime('%Y%m%d'))
            if c:self.candidates[code]=c
        self.scanned+=1
        if self.scanned%20==0:self.log(f'전체 스캔 {self.scanned}/{len(self.symbols)} 종목, 조건 충족 {len(self.candidates)}')
        self.refresh_tables()
        self.set_status('전체 종목 지표 분석 중')

    def on_data(self,kind,data):
        try:
            if kind=='stocks':
                self.symbols=data;self.cursor=0;self.scanned=0
                self.log('전체 주식 '+str(len(data))+'종목 조회 완료. 순차 일봉 스캔 진행')
                self.needs_reconcile=True
            elif kind=='bars':self.process_bars(self.last_symbol,data)
            elif kind in ('quote','tick'):self.process_quote(data)
            elif kind=='holdings':self.receive_holdings(data)
            elif kind=='order':
                if not self.pending_intent:raise RuntimeError('의도 없는 주문 회신')
                self.pending_ack=True
                self.ledger.record('ORDER_REPLY',id=self.pending_intent.intent_id,
                    order_number=data['number'],message=data['message'],kind=data['kind'])
                self.pending_intent.order_number=data['number']
                self.log('주문번호/메시지 수신 (체결 아님): '+data['number']+' / '+data['message'])
                self.needs_reconcile=True
            self.refresh_tables()
        except Exception as exc:
            self.log('서버 응답 분석 오류: '+str(exc));self.halt_all()

    def receive_holdings(self, holdings):
        # The balance query is authoritative for quantity, NOT order execution price.
        self.holdings=holdings;self.last_account_sync=datetime.now()
        self.needs_reconcile=False
        if any(h.pending_buy or h.pending_sell for h in holdings.values()):
            self.log('계좌에 미체결 주문이 있습니다. 새로운 주문은 보류됩니다.')
        if any(h.credit_qty>0 for h in holdings.values()):
            self.log('계좌 신용잔고 감지 — 신규 주문 차단');self.halt_all();return
        if self.pending_intent:
            i=self.pending_intent
            state,qty,price=reconcile_holdings(i,holdings)
            if state in ('FILLED','PARTIAL_TERMINAL','SELL_NEEDS_EXECUTION_PRICE'):
                if state=='SELL_NEEDS_EXECUTION_PRICE':
                    # Execution price is NOT in SABA200QB. Log reference as an estimate.
                    price=i.reference_price
                    state='FILLED' if qty==i.qty else 'PARTIAL_TERMINAL'
                    self.log('매도 체결가 미확인: 주문 당시 참고가로 잠정 기록. 실제 손익 HTS 확인')
                self.ledger.final(i,state,qty,price,i.order_number)
                self.pending_intent=None;self.pending_ack=False
                self.log(f'계좌 보유수량 변화 확인: {i.side} {i.code} {qty}주 / {state}')
                # User must refresh cash purchasing power on next buy (no verified cash TR).
                self.cash_proof=None
            elif state=='CONFLICT':
                self.log('계좌 수량 불일치. 미체결 주문 확인 후 수동 정산해야 함')
                self.halt_all()
            elif self.pending_since and (datetime.now()-self.pending_since).total_seconds()>120:
                self.log('주문 120초 후에도 체결/거부를 확정할 수 없어 중지. HTS 확인')
                self.halt_all()
        for code,p in self.ledger.positions.items():
            h=holdings.get(code)
            if not h or h.qty<p.qty or h.credit_qty>0:
                self.log('자동매매 관리분과 신한 잔고 불일치: '+code)
                self.halt_all();break

    def process_quote(self,q):
        if not self.armed or self.pending_intent is not None:return
        self.quotes[q.code]=q
        now=self.now()
        if not quote_fresh(q,now,self.cfg):return
        if q.code in self.ledger.positions:
            p=self.ledger.positions[q.code]
            p.high_since_entry=max(p.high_since_entry,q.price)
            # Preserve highest price on disk for restart safety.
            if self.mode=='LIVE':self.ledger.persist_positions()
            reason=sell_reason(p,q,now,self.cfg)
            if reason:
                h=self.holdings.get(q.code) if self.mode=='LIVE' else Holding(q.code,p.name,p.qty,p.avg_price)
                if not h or h.qty<p.qty or h.pending_sell or h.pending_buy or h.credit_qty:
                    self.log('매도조건 발생했으나 잔고/미체결 미확인으로 주문 차단');return
                self.place('SELL',q.code,p.name,p.qty,q.price,h.qty,reason)
            return
        if not self.buy_allowed:return
        if self.daily_start_equity and self.equity() < self.daily_start_equity*(1-self.cfg.daily_loss_limit_pct):
            self.buy_allowed=False;self.log('일일 평가손실 한도 도달: 매수 중단, 매도 감시 유지');return
        c=self.candidates.get(q.code)
        if c is None or not buy_trigger(c,q,now,self.cfg):return
        if q.code in [x.get('code') for x in self.ledger.entries
                      if x.get('type')=='ORDER_INTENT' and x.get('side')=='BUY'
                      and x.get('time','')[:10]==now.date().isoformat()]:return
        if self.ledger.daily_order_count(now.date().isoformat())>=self.cfg.max_daily_orders:return
        if self.mode=='LIVE':
            if self.last_account_sync is None or (datetime.now()-self.last_account_sync).total_seconds()>60:
                self.needs_reconcile=True;return
            if any(h.pending_buy or h.pending_sell or h.credit_qty for h in self.holdings.values()):return
            if self.holdings.get(q.code,Holding(q.code,c.name,0,0)).qty>0:return
        cash=self.available_cash()
        qty=order_quantity(q.price,cash,self.equity(),self.cfg,len(self.ledger.positions),
                           self.ledger.daily_buy_committed(now.date().isoformat()))
        if qty>0:
            before=self.holdings.get(q.code,Holding(q.code,c.name,0,0)).qty
            self.place('BUY',q.code,c.name,qty,q.price,before,'20일 고점 돌파')
        elif self.mode=='LIVE' and cash is None:
            self.log('신규매수 차단: 신한 현금100% 주문가능액이 5분 내 재확인되지 않았습니다.')

    def place(self,side,code,name,qty,price,before,reason):
        if not self.armed or self.pending_intent or self.ledger.unresolved():return
        if self.ledger.daily_order_count(self.now().date().isoformat())>=self.cfg.max_daily_orders:return
        if not market_window(self.now(),side):return
        i=self.ledger.new_intent(side,code,name,qty,price,before)
        self.pending_intent=i;self.pending_since=datetime.now();self.pending_ack=False
        self.log(f'{self.mode} {side}: {name}({code}), {qty}주, 참고 {fmt(price)}원 / {reason}')
        if self.mode=='DEMO':
            self.fake_execute(i)
            return
        if not self.broker.order(self.account.text(),self.password.text(),side,code,qty,price):
            self.log('주문 전송 결과 불명확: 중복 재시도 금지. 신한 HTS 직접 확인')
            self.halt_all()
        else:
            self.needs_reconcile=True
            if self.cash_proof and side=='BUY':
                self.cash_proof['used']+=int(price*qty*1.03)

    def fake_execute(self,i):
        # Paper-only: deterministic immediate fill at reference price; no network/orders.
        price=i.reference_price
        if i.side=='BUY':
            cost=price*i.qty
            if cost>self.sim_cash:
                self.log('모의 잔액부족으로 거절');self.ledger.final(i,'REJECTED_VERIFIED',0,0)
            else:
                self.sim_cash-=cost
                self.ledger.final(i,'FILLED',i.qty,price,'PAPER-'+i.intent_id[:6])
        else:
            self.sim_cash+=price*i.qty
            self.ledger.final(i,'FILLED',i.qty,price,'PAPER-'+i.intent_id[:6])
        self.pending_intent=None;self.pending_since=None
        self.log(f'가상 주문 처리 완료. 가상 현금잔고 {fmt(self.sim_cash)}원')
        self.refresh_tables()

    def refresh_tables(self):
        cands=sorted(self.candidates.values(),key=lambda c:c.score,reverse=True)[:30]
        self.scan_table.setRowCount(len(cands))
        for r,c in enumerate(cands):
            for col,val in enumerate((c.code,c.name,f'{c.score:.2f}',fmt(c.breakout),fmt(c.avg_value))):
                self.scan_table.setItem(r,col,QTableWidgetItem(str(val)))
        pos=list(self.ledger.positions.values())
        self.pos_table.setRowCount(len(pos))
        for r,p in enumerate(pos):
            last=self.quotes.get(p.code)
            px=last.price if last else p.avg_price
            pct=(px/p.avg_price-1)*100 if p.avg_price else 0
            for col,val in enumerate((p.code,p.name,fmt(p.qty),fmt(p.avg_price),fmt(px),f'{pct:+.2f}%')):
                self.pos_table.setItem(r,col,QTableWidgetItem(str(val)))

    def closeEvent(self,event):
        if self.armed:
            answer=QMessageBox.warning(self,'자동매매 프로그램 종료',
                '프로그램이 종료되면 손절·익절을 감시하지 않습니다.\n종료하시겠습니까?',
                QMessageBox.Yes|QMessageBox.No,QMessageBox.No)
            if answer!=QMessageBox.Yes:event.ignore();return
        self.halt_all()
        event.accept()


if __name__=='__main__':
    app=QApplication(sys.argv)
    w=Window();w.show()
    sys.exit(app.exec_())
