using System;
using System.Collections.Generic;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class BrokerReadinessTests {
        static int checks;
        static readonly DateTime Now=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
        static readonly string Fingerprint=Convert.ToBase64String(new byte[32]);
        sealed class Fixture {
            public BrokerReadiness Data=new BrokerReadiness(new string('a',64),Fingerprint);
            public LiveOrderJournalState Journal=new LiveOrderJournalState{AccountFingerprint=Fingerprint};
            public AccountCashSnapshot Cash;
            public BrokerOrdersSnapshot Orders;
            public BrokerHoldingsSnapshot Balance;
            public AccountOrderCapacity Capacity;
            public Fixture(){Data.SetAuthorization(true);Data.SetAccountStream(true,Now.AddSeconds(-10));Refresh(Now);}
            public void Refresh(DateTime at) {
                foreach(var code in new[]{"000660","005930","000880"})Data.ObserveTradingDate(code,"20261112",at.AddSeconds(-8),at.AddSeconds(-7));
                Balance=Positions(at);Data.ObserveTradeBalance(Balance);
                Cash=new AccountCashSnapshot{DepositCash=1000000,CashOrderable=1000000,Cash100Orderable=1000000,ReceivedUtc=at.AddSeconds(-2)};
                Data.ObserveCash(Cash,at.AddSeconds(-3));
                Orders=new BrokerOrdersSnapshot{TradingDate="20261112",ReceivedUtc=at.AddSeconds(-1)};Data.ObserveOrders(Orders,at.AddSeconds(-2));
                Capacity=Cap("000660",at);Data.ObserveCapacity(Capacity,at.AddSeconds(-1));
            }
            public BrokerReadinessResult Evaluate(LiveOrderIntent intent=null,DateTime? at=null,decimal limit=1000000,bool live=true,bool price=true) {
                var now=at??Now;var order=intent??Buy();var q=new IndiReply{Code=order.Code,Price=10000,Clock=MarketTime.ToKorea(now).ToString("HHmmss"),HasTradeClock=true};
                return Data.Evaluate(order,q,now,live,price,Journal,limit,now);
            }
        }
        static BrokerHoldingsSnapshot Positions(DateTime at) {
            var b=new BrokerHoldingsSnapshot{BrokerDate="12",BrokerDateValidated=false,RequestStartedUtc=at.AddSeconds(-4),ReceivedUtc=at.AddSeconds(-3)};
            b.Holdings.Add(new Holding{Code="005930",Quantity=2,AvgPrice=70000,Price=70000});b.SellableQuantities["005930"]=2;b.UnfilledQuantities["005930"]=0;b.CashSourceValidated["005930"]=true;return b;
        }
        static AccountOrderCapacity Cap(string code,DateTime at){return new AccountOrderCapacity{Code=code,LimitPrice=10000,Ratio=100,Quantity=100,MaximumQuantity=100,Amount=1000000,MaximumAmount=1000000,ReceivedUtc=at.AddMilliseconds(-500)};}
        static LiveOrderIntent Buy(int qty=1){return new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code="000660",Quantity=qty,LimitPrice=10000,ClientKey="new-entry"};}
        static LiveOrderIntent Sell(int qty=1){return new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="005930",Quantity=qty,LimitPrice=10000,ClientKey="new-exit"};}
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("BrokerReadinessTests: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,name);}
        static LiveOrderRecord Working(int filled=0){return new LiveOrderRecord{Id="known-local",ClientKey="prior-entry",Code="000880",BrokerOrderNumber="11",TradingDate="20261112",Kind=LiveOrderKind.Buy,TradeKind=LiveOrderKind.Buy,Status=filled==0?LiveOrderStatus.Working:LiveOrderStatus.PartiallyFilled,Quantity=10,FilledQuantity=filled,LimitPrice=10000,CreatedUtc=Now.AddSeconds(-20),UpdatedUtc=Now.AddSeconds(-6)};}
        static void Known(Fixture f,int filled=0) {
            f.Journal.Orders.Add(Working(filled));f.Journal.LastBrokerChangeUtc=Now.AddSeconds(-6);
            var balance=Positions(Now.AddMilliseconds(100));balance.Holdings.Add(new Holding{Code="000880",Quantity=filled,AvgPrice=filled==0?0:9950,Price=10000});
            balance.SellableQuantities["000880"]=filled;balance.UnfilledQuantities["000880"]=10-filled;balance.CashSourceValidated["000880"]=true;
            f.Data.ObserveTradeBalance(balance);
            var orders=new BrokerOrdersSnapshot{TradingDate="20261112",ReceivedUtc=Now.AddMilliseconds(-100)};
            orders.Orders.Add(new BrokerOrderRow{OrderNumber="11",Code="000880",SideCode="2",ProductCode="01",CreditCode="00",Quantity=10,LimitPrice=10000,FilledQuantity=filled,OpenQuantity=10-filled,AverageFillPrice=filled==0?0:9950});
            f.Orders=orders;f.Data.ObserveOrders(orders,Now.AddMilliseconds(-200));
        }
        public static int Run() {
            checks=0;var f=new Fixture();var result=f.Evaluate();
            Check(result.Ready&&result.Checks.AccountAuthorized&&result.ReconciliationScope=="NoLocalUnresolvedBaseline","fresh empty baseline can authorize data readiness without inventing historical fills");
            Check(!f.Data.AccountStreamObserved&&result.Checks.FillsReconciled,"first order does not require a prior AA/AD change that can only occur after trading");
            Check(!f.Orders.IsComplete&&!f.Orders.HasExecutionIds,"runtime reconciliation does not turn aggregate DTOs into full execution evidence");
            f.Data.SetAuthorization(false);Check(!f.Evaluate().Ready,"local user authorization remains a separate required condition");f.Data.SetAuthorization(true);
            Check(!f.Evaluate(live:false).Ready,"query snapshots cannot stand in for advancing live quote events");
            Check(!f.Evaluate(price:false).Ready,"unproven price rules keep the affected order pending");
            var altered=Buy();altered.LimitPrice=9990;Check(!f.Evaluate(altered).Ready,"a valid live trade price does not certify an arbitrary amended price");
            Check(!f.Evaluate(at:Now.AddSeconds(6)).Ready,"old account responses expire even while quote events keep arriving");
            f.Cash.Cash100Orderable=0;Check(f.Evaluate().Ready,"mutating an input DTO cannot silently change an accepted evidence snapshot");
            var old=new AccountCashSnapshot{Cash100Orderable=-1,ReceivedUtc=Now.AddSeconds(-4)};
            Check(!f.Data.ObserveCash(old,Now.AddSeconds(-5))&&f.Evaluate().Ready,"late older responses cannot replace newer account evidence");
            f.Data.ObserveTradingDate("000660","20261111",Now.AddMilliseconds(-300),Now.AddMilliseconds(-200));
            Check(!f.Evaluate().Ready,"the actual symbol's full bar date must match today's KST trading day");
            f=new Fixture();f.Data.ObserveTradingDate("000660","12",Now.AddMilliseconds(-300),Now.AddMilliseconds(-200));
            Check(!f.Evaluate().Ready,"a two-character balance day is never expanded using the PC calendar");
            f=new Fixture();Check(f.Data.BrokerDateDiagnostic=="12"&&!f.Data.BrokerDateDiagnosticIsFullCalendarDate&&f.Evaluate().Ready,"raw balance day is diagnostic only; an actual full bar date anchors the live session");
            f=new Fixture();f.Journal.AccountFingerprint=Convert.ToBase64String(Enumerable.Repeat((byte)1,32).ToArray());
            Check(!f.Evaluate().Ready,"journal identity cannot cross account sessions");

            f=new Fixture();f.Data.ObserveAccountEvent(Now);
            var after=Now.AddSeconds(5);f.Refresh(after);
            var delayed=new AccountCashSnapshot{Cash100Orderable=1000000,ReceivedUtc=Now.AddSeconds(4)};
            f.Data.ObserveCash(delayed,Now.AddSeconds(-1));
            Check(!f.Evaluate(at:after).Ready,"pre-change request finishing late does not clear an AA/AD invalidation barrier");
            var fresh=new AccountCashSnapshot{Cash100Orderable=1000000,ReceivedUtc=Now.AddSeconds(4.5)};
            f.Data.ObserveCash(fresh,Now.AddSeconds(4));Check(f.Evaluate(at:after).Ready,"all newly requested consistent snapshots can clear the change barrier");
            f.Data.Invalidate(Now.AddSeconds(-3),"account-password-SECRET");Check(f.Data.InvalidatedUtc==Now,"out of order changes cannot move the invalidation boundary backwards");
            Check(!f.Evaluate(at:after).Status.Contains("SECRET"),"provider text is never reused as a readiness explanation");

            f=new Fixture();f.Journal.Orders.Add(Working());f.Journal.Orders[0].Status=LiveOrderStatus.Unknown;
            Check(!f.Evaluate().Ready&&!f.Evaluate().Checks.RecoveryVerified,"an empty broker response cannot clear an unknown local order");
            f.Journal.Orders[0].Status=LiveOrderStatus.AwaitingAcknowledgement;Check(!f.Evaluate().Ready,"an accepted request is not an acknowledged working order");
            f.Journal.Orders[0].Status=LiveOrderStatus.Working;f.Journal.Orders[0].TradingDate="20261111";
            Check(!f.Evaluate().Ready,"previous day outstanding orders do not disappear at midnight");

            f=new Fixture();var notSent=Working();notSent.Id="local-abort";notSent.Code="000660";notSent.ClientKey="not-sent/local-abort";
            notSent.OriginalClientKey="new-entry";notSent.BrokerOrderNumber=null;notSent.Status=LiveOrderStatus.NotSent;f.Journal.Orders.Add(notSent);
            Check(f.Evaluate().Ready,"a proven pre-RequestData abort has no broker order to reconcile and permits fresh evaluation of its original strategy key");
            notSent.RequestId=7;Check(!f.Evaluate().Ready,"a purported not-sent record with an accepted request identifier cannot bypass reconciliation");

            f=new Fixture();Known(f);
            Check(f.Evaluate().Ready&&f.Evaluate().ReconciliationScope=="KnownLocalOrdersAndExecutions","known open orders can be reconciled by exact broker rows and zero-fill evidence");
            Check(!f.Evaluate(Buy(6),limit:150000).Ready&&f.Evaluate(Buy(5),limit:150000).Ready,"pending buys reserve operating capital across different symbols");
            f.Journal.Orders[0].ClientKey="new-entry";Check(!f.Evaluate().Ready,"a reused order key is rejected before transmission");

            f=new Fixture();Known(f,3);
            Check(!f.Evaluate().Ready,"aggregate filled quantities cannot fabricate missing execution identifiers");
            var execution=new LiveExecutionRecord{OrderId="known-local",BrokerOrderNumber="11",TradingDate="20261112",ExecutionId="F1",Quantity=3,Price=9950,ExecutedUtc=Now.AddSeconds(-6)};
            f.Journal.Executions.Add(execution);Check(f.Evaluate().Ready,"unique real execution evidence can explain an exact aggregate cumulative fill");
            f.Journal.Executions.Add(execution);Check(!f.Evaluate().Ready,"duplicate execution identifiers cannot double count a fill");f.Journal.Executions.RemoveAt(1);
            f.Orders.Orders[0].FilledQuantity=4;f.Orders.Orders[0].OpenQuantity=6;f.Orders.ReceivedUtc=Now;f.Data.ObserveOrders(f.Orders,Now.AddMilliseconds(-10));
            Check(!f.Evaluate().Ready,"a broker cumulative quantity change without matching executions invalidates readiness");

            f=new Fixture();var external=new BrokerOrdersSnapshot{TradingDate="20261112",ReceivedUtc=Now};
            external.Orders.Add(new BrokerOrderRow{OrderNumber="99",Code="005930",SideCode="1",Quantity=1,LimitPrice=10000,OpenQuantity=1});f.Data.ObserveOrders(external,Now.AddMilliseconds(-100));
            Check(!f.Evaluate().Ready,"outside-program open orders cannot be treated as local reconciliation evidence");
            f=new Fixture();var pending=Positions(Now.AddMilliseconds(100));pending.UnfilledQuantities["005930"]=1;f.Data.ObserveTradeBalance(pending);
            Check(!f.Evaluate().Ready,"neutral broker pending quantity must match known open order rows");

            f=new Fixture();Check(f.Evaluate(Sell(2)).Ready&&f.Evaluate(Sell(2)).Checks.SellableQuantity==2,"a supported fresh trade-basis quantity can support a bounded cash-stock sale");
            Check(!f.Evaluate(Sell(3)).Ready,"settlement or total holding quantity cannot expand verified sellable quantity");
            var unsupported=Positions(Now.AddMilliseconds(100));unsupported.CashSourceValidated["005930"]=false;f.Data.ObserveTradeBalance(unsupported);
            Check(!f.Evaluate(Sell()).Ready,"unverified holding kinds do not become supported because a quantity is positive");
            f=new Fixture();f.Data.ObserveBalanceSellable("005930",1,Now.AddSeconds(-5));
            Check(!f.Evaluate(Sell()).Ready,"a fresh AD quantity disagreement blocks the affected sale");
            f.Data.ObserveBalanceSellable("005930",2,Now.AddSeconds(-4.9));
            Check(f.Evaluate(Sell(2)).Ready&&f.Data.AccountStreamObserved,"matching observed AD data corroborates the current trade-basis snapshot");
            f.Data.SetAccountStream(false,Now);Check(!f.Evaluate().Ready,"stream loss invalidates previously accepted account snapshots");

            f=new Fixture();result=f.Evaluate();
            Check(result.Ready&&result.EvidenceStillCurrent()!=false&&object.ReferenceEquals(result.Checks.EvidenceStillCurrent,result.EvidenceStillCurrent),"ready checks carry their account evidence epoch guard directly");
            f.Data.ObserveCash(new AccountCashSnapshot{Cash100Orderable=900000,ReceivedUtc=Now.AddMilliseconds(100)},Now.AddMilliseconds(-100));
            Check(!result.EvidenceStillCurrent(),"a cash response pumped while COM fields are being set invalidates the older proof");
            var next=f.Evaluate(at:Now.AddMilliseconds(200));Check(next.Ready&&next.EvidenceStillCurrent(),"a new evaluation can use the newer cash evidence without reusing the old proof");
            f.Data.SetAccountStream(true,Now.AddMilliseconds(300));Check(next.EvidenceStillCurrent(),"stable registration status does not advance the financial evidence epoch");
            f.Data.SetAuthorization(false);Check(!next.EvidenceStillCurrent(),"local revocation invalidates already-built order checks");

            f=new Fixture();Known(f);f.Orders.Orders[0].OrderNumber="00011";f.Orders.ReceivedUtc=Now;f.Data.ObserveOrders(f.Orders,Now.AddMilliseconds(-10));
            Check(f.Evaluate().Ready&&f.Journal.Orders[0].BrokerOrderNumber=="11","zero-padded numeric broker numbers match without mutating the journal's original identifier");
            var ambiguous=new BrokerOrdersSnapshot{TradingDate="20261112",ReceivedUtc=Now};
            ambiguous.Orders.Add(new BrokerOrderRow{OrderNumber="11",Code="000880",SideCode="2",Quantity=10,LimitPrice=10000,OpenQuantity=10});
            ambiguous.Orders.Add(new BrokerOrderRow{OrderNumber="00011",Code="000880",SideCode="2",Quantity=10,LimitPrice=10000,OpenQuantity=10});
            Reject(()=>f.Data.ObserveOrders(ambiguous,Now.AddMilliseconds(-10)),"canonical duplicate identifiers make the broker snapshot ambiguous");
            var duplicate=Working();duplicate.Id="other-local";duplicate.ClientKey="other-key";duplicate.BrokerOrderNumber="00011";f.Journal.Orders.Add(duplicate);
            Check(!f.Evaluate().Ready,"two local records cannot claim one canonical broker identifier");

            f=new Fixture();Known(f);f.Orders.Orders[0].ProductCode="70";f.Orders.ReceivedUtc=Now;f.Data.ObserveOrders(f.Orders,Now.AddMilliseconds(-10));
            Check(!f.Evaluate().Ready,"an order row from another account product cannot certify a domestic cash order");
            f=new Fixture();Known(f);f.Orders.Orders[0].CreditCode="01";f.Orders.ReceivedUtc=Now;f.Data.ObserveOrders(f.Orders,Now.AddMilliseconds(-10));
            Check(!f.Evaluate().Ready,"a credit order row cannot certify the cash-only journal route");
            f=new Fixture();Known(f);f.Orders.Orders[0].OriginalOrderNumber="77";f.Orders.ReceivedUtc=Now;f.Data.ObserveOrders(f.Orders,Now.AddMilliseconds(-10));
            Check(!f.Evaluate().Ready,"an unrelated amendment chain cannot replace a new order's identity");

            Reject(()=>new BrokerReadiness("12345678901",Fingerprint),"readiness never uses plaintext account numbers as stored account keys");
            Reject(()=>f.Data.ObserveCash(new AccountCashSnapshot{ReceivedUtc=Now.AddSeconds(-1)},Now),"a response cannot precede its request");
            Reject(()=>f.Data.Invalidate(DateTime.SpecifyKind(Now,DateTimeKind.Local)),"local clock timestamps cannot masquerade as UTC evidence");
            return checks;
        }
    }
}
