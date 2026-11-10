import sys, pathlib, tempfile, unittest
from datetime import datetime, timedelta, time
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]))
from engine import (Settings,Bar,Symbol,Quote,Holding,Position,Ledger,Intent,
                    market_window,make_candidate,buy_trigger,sell_reason,
                    order_quantity,reconcile_holdings)

class EngineTests(unittest.TestCase):
    def setUp(self):
        self.cfg=Settings(min_trade_value=100000)
        self.now=datetime(2026,11,10,10,30)
        self.today='20261110'

    def bars(self):
        start=datetime(2026,7,1)
        return [Bar((start+timedelta(days=i)).strftime('%Y%m%d'),
                    8000+i*45+80,8000+i*45-80,8000+i*45,200_000) for i in range(65)]

    def candidate(self):
        return make_candidate(Symbol('005930','삼성전자','0'),self.bars(),self.cfg,self.today)

    def test_candidate(self):
        c=self.candidate()
        self.assertIsNotNone(c)
        self.assertEqual(c.code,'005930')
        self.assertGreater(c.breakout,c.yclose)

    def test_candidate_need_40(self):
        self.assertIsNone(make_candidate(Symbol('005930','삼성전자','0'),self.bars()[:20],self.cfg,self.today))

    def test_todays_bar_excluded(self):
        b=self.bars()
        b.append(Bar('20261110',1000000,1,1000000,200000))
        c=make_candidate(Symbol('005930','삼성전자','0'),b,self.cfg,self.today)
        self.assertLess(c.breakout,20000)

    def test_buy_trigger(self):
        c=self.candidate()
        q=Quote(c.code,c.breakout+20,self.now,True)
        self.assertTrue(buy_trigger(c,q,self.now,self.cfg))
        self.assertFalse(buy_trigger(c,Quote(c.code,c.breakout-1,self.now,True),self.now,self.cfg))
        self.assertFalse(buy_trigger(c,Quote(c.code,c.breakout+20,self.now-timedelta(seconds=30),True),self.now,self.cfg))
        self.assertFalse(buy_trigger(c,Quote(c.code,c.breakout+20,self.now,False),self.now,self.cfg))

    def test_market_time(self):
        self.assertTrue(market_window(self.now,'BUY'))
        self.assertFalse(market_window(self.now.replace(hour=14,minute=50),'BUY'))
        self.assertTrue(market_window(self.now.replace(hour=14,minute=50),'SELL'))
        self.assertFalse(market_window(datetime(2026,11,8,10,30),'SELL'))

    def test_sells(self):
        p=Position('005930','삼성전자',10,10000,11000,'2026-11-10')
        self.assertEqual(sell_reason(p,Quote(p.code,9300,self.now,True),self.now,self.cfg),'손절')
        self.assertEqual(sell_reason(p,Quote(p.code,11500,self.now,True),self.now,self.cfg),'익절')
        self.assertEqual(sell_reason(p,Quote(p.code,10000,self.now,True),self.now,self.cfg),'추적손절')
        self.assertIsNone(sell_reason(p,Quote(p.code,10100,self.now,False),self.now,self.cfg))

    def test_cash_unknown_blocks(self):
        self.assertEqual(order_quantity(10000,None,1_000_000,self.cfg,0,0),0)
        self.assertGreater(order_quantity(10000,1_000_000,1_000_000,self.cfg,0,0),0)

    def test_position_limit(self):
        self.assertEqual(order_quantity(10000,1_000_000,1_000_000,self.cfg,3,0),0)

    def test_journal_no_duplicate_if_crash(self):
        with tempfile.TemporaryDirectory() as p:
            store=Ledger(pathlib.Path(p))
            i=store.new_intent('BUY','005930','삼성전자',10,10000,0)
            self.assertEqual(store.unresolved(),[i.intent_id])
            restarted=Ledger(pathlib.Path(p))
            with self.assertRaises(RuntimeError):
                restarted.new_intent('BUY','005930','삼성전자',10,10000,0)

    def test_buy_fill_recovery(self):
        with tempfile.TemporaryDirectory() as p:
            path=pathlib.Path(p)
            store=Ledger(path)
            i=store.new_intent('BUY','005930','삼성전자',10,10000,0)
            store.final(i,'FILLED',10,10100,'paper')
            recovered=Ledger(path)
            self.assertEqual(recovered.positions['005930'].qty,10)
            self.assertEqual(recovered.positions['005930'].avg_price,10100)
            self.assertFalse(recovered.unresolved())

    def test_sell_full_liquidation(self):
        with tempfile.TemporaryDirectory() as p:
            store=Ledger(pathlib.Path(p))
            buy=store.new_intent('BUY','005930','삼성전자',3,10000,0)
            store.final(buy,'FILLED',3,10000,'1')
            sell=store.new_intent('SELL','005930','삼성전자',3,11000,3)
            result=reconcile_holdings(sell,{})
            self.assertEqual(result[0],'SELL_NEEDS_EXECUTION_PRICE')
            self.assertEqual(result[1],3)
            store.final(sell,'FILLED',3,11000,'2')
            self.assertEqual(Ledger(pathlib.Path(p)).positions,{})

    def test_reconcile_pending_never_fill(self):
        i=Intent('xyz','BUY','005930','삼성전자',10,10000,0,self.now.isoformat())
        h=Holding('005930','삼성전자',5,10000,pending_buy=5)
        self.assertEqual(reconcile_holdings(i,{'005930':h})[0],'WAIT')
        self.assertEqual(reconcile_holdings(i,{})[0],'WAIT')

    def test_sell_does_not_allow_excess(self):
        i=Intent('xyz','SELL','005930','삼성전자',10,10000,10,self.now.isoformat())
        self.assertEqual(reconcile_holdings(i,{'005930':Holding('005930','삼성전자',100,10000)})[0],'CONFLICT')

if __name__=='__main__':unittest.main()
