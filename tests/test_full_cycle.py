"""Offline full cycle: universe -> signals -> buy -> restart -> take-profit sell."""
import sys,pathlib,tempfile,unittest
from datetime import datetime,timedelta
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]))
from engine import Settings,Ledger,Symbol,Bar,Quote,make_candidate,buy_trigger,order_quantity,sell_reason


class CycleTest(unittest.TestCase):
    def test_full_entry_exit_and_relaunch(self):
        cfg=Settings(min_trade_value=100000)
        now=datetime(2026,11,10,10,30)
        rows=[]
        for i in range(55):
            px=10000+i*50
            rows.append(Bar((datetime(2026,8,1)+timedelta(days=i)).strftime('%Y%m%d'),
                            int(px*1.006),int(px*.994),px,400000))
        c=make_candidate(Symbol('005930','테스트 주식','0'),rows,cfg,'20261110')
        self.assertIsNotNone(c)
        buy_price=round(c.breakout*1.01)
        q=Quote('005930',buy_price,now,True)
        self.assertTrue(buy_trigger(c,q,now,cfg))
        with tempfile.TemporaryDirectory() as path:
            store=Ledger(pathlib.Path(path))
            cash=2_000_000
            qty=order_quantity(buy_price,cash,cash,cfg,0,0)
            self.assertGreater(qty,0)
            buy=store.new_intent('BUY',c.code,c.name,qty,buy_price,0)
            store.final(buy,'FILLED',qty,buy_price,'PAPER-1')
            cash-=qty*buy_price
            restarted=Ledger(pathlib.Path(path))
            p=restarted.positions[c.code]
            self.assertEqual(p.qty,qty)
            sale_price=int(buy_price*1.125)
            self.assertEqual(sell_reason(p,Quote(c.code,sale_price,now,True),now,cfg),'익절')
            sell=restarted.new_intent('SELL',c.code,c.name,p.qty,sale_price,qty)
            restarted.final(sell,'FILLED',qty,sale_price,'PAPER-2')
            cash+=qty*sale_price
            again=Ledger(pathlib.Path(path))
            self.assertEqual(again.positions,{})
            self.assertEqual(again.unresolved(),[])
            self.assertGreater(cash,2_000_000)

if __name__=='__main__':unittest.main()
