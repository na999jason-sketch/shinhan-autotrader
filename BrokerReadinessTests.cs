using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShinhanNativeTrader {
    public static class BrokerReadinessTests {
        static int checks;
        static readonly DateTime Now=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
        static readonly string Fingerprint=Convert.ToBase64String(new byte[32]);
        sealed class HoldingsResponse : IIndiTransport {
            public event Action<int> ReceiveData {add{} remove{}}
            public event Action<int> ReceiveSysMsg {add{} remove{}}
            public event Action<string> Error {add{} remove{}}
            public bool BasisUnavailable;
            public readonly Dictionary<int,object> Fields=new Dictionary<int,object> {
                {0,"A005930"},{1,"Test holding"},{2,""},{3,"보통"},{4,"2"},
                {10,"2"},{11,"0"},{12,"70000"},{13,"70000"},{22,"0"}
            };
            public object Invoke(string name,params object[] args) {
                if(name=="GetMultiRowCount")return 1;
                if(name=="GetSingleData")return "";
                if(name!="GetMultiData"||Convert.ToInt32(args[0])!=0)throw new InvalidOperationException("Unexpected offline holdings getter.");
                var field=Convert.ToInt32(args[1]);
                if(field==2&&BasisUnavailable)throw new InvalidOperationException("Optional metadata unavailable.");
                object value;return Fields.TryGetValue(field,out value)?value:"";
            }
            public void Dispose(){}
        }
        static BrokerHoldingsSnapshot ParsedBalance(string basis,bool requestBound,Action<HoldingsResponse> configure=null) {
            var io=new HoldingsResponse();io.Fields[2]=basis;if(configure!=null)configure(io);
            var snapshot=AccountQueries.ReadHoldings(io,Now.AddMilliseconds(-100),requestBound);
            snapshot.RequestStartedUtc=Now.AddMilliseconds(-200);return snapshot;
        }
        static BrokerHoldingsSnapshot OrdinaryBalance(bool requestBound=true,Action<HoldingsResponse> configure=null) {
            var io=new HoldingsResponse();io.Fields[2]="5";io.Fields[3]="";io.Fields[22]="";if(configure!=null)configure(io);
            var snapshot=AccountQueries.ReadHoldings(io,Now.AddMilliseconds(-90),false,requestBound);
            snapshot.RequestStartedUtc=Now.AddMilliseconds(-190);return snapshot;
        }
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
            var b=new BrokerHoldingsSnapshot{BrokerDate="12",BrokerDateValidated=false,BasisVerified=true,RequestStartedUtc=at.AddSeconds(-4),ReceivedUtc=at.AddSeconds(-3)};
            b.Holdings.Add(new Holding{Code="005930",Quantity=2,AvgPrice=70000,Price=70000});b.SellableQuantities["005930"]=2;b.UnfilledQuantities["005930"]=0;b.CashSourceValidated["005930"]=true;b.BasisValidated["005930"]=true;return b;
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
            balance.SellableQuantities["000880"]=filled;balance.UnfilledQuantities["000880"]=10-filled;balance.CashSourceValidated["000880"]=true;balance.BasisValidated["000880"]=true;
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

            f=new Fixture();var priorReady=f.Evaluate();var unknownBasis=Positions(Now.AddMilliseconds(100));unknownBasis.BasisVerified=false;
            Check(f.Data.ObserveTradeBalance(unknownBasis),"unknown basis data can replace the display snapshot without being rejected as a transport failure");
            Check(!f.Evaluate().Ready&&!f.Evaluate().Checks.OrdersReconciled,"unknown holding basis cannot certify account exposure for a new buy");
            Check(!f.Evaluate(Sell()).Ready,"unknown holding basis cannot authorize a sale even when the numeric quantity is positive");
            Check(!priorReady.EvidenceStillCurrent(),"an accepted unknown-basis response invalidates an older ready proof");
            var mismatchedBasis=Positions(Now.AddMilliseconds(200));mismatchedBasis.BasisValidated["005930"]=false;f.Data.ObserveTradeBalance(mismatchedBasis);
            Check(!f.Evaluate().Ready,"an aggregate basis flag cannot override an unverified individual position");
            f.Data.ObserveTradeBalance(Positions(Now.AddMilliseconds(300)));Check(f.Evaluate().Ready,"a later explicitly verified trade-basis frame can restore data readiness");
            f=new Fixture();var empty=new BrokerHoldingsSnapshot{RequestStartedUtc=Now.AddMilliseconds(-200),ReceivedUtc=Now.AddMilliseconds(-100)};f.Data.ObserveTradeBalance(empty);
            Check(!f.Evaluate().Ready,"an unbound empty query response is not assumed to be a complete trade-basis balance");
            empty=new BrokerHoldingsSnapshot{BasisVerified=true,RequestStartedUtc=Now.AddMilliseconds(-100),ReceivedUtc=Now};f.Data.ObserveTradeBalance(empty);
            Check(f.Evaluate().Ready,"an explicitly request-bound empty trade-basis frame permits a new buy after the last position has been sold");

            foreach(var basis in new[]{"","0","9"}) {
                f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance(basis,true));
                Check(f.Evaluate().Ready&&f.Evaluate(Sell(2)).Ready,
                    "parser-produced request-bound holdings can support buys and sales despite missing or unknown optional MO2 metadata");
            }
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>io.BasisUnavailable=true));
            Check(f.Evaluate().Ready&&f.Evaluate(Sell(2)).Ready,
                "an unavailable optional MO2 getter does not override accepted trade-basis request context");
            foreach(var basis in new[]{"","1"}) {
                f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance(basis,false));
                Check(!f.Evaluate().Ready&&!f.Evaluate(Sell()).Ready,
                    "a parser snapshot without bound accepted request context cannot authorize orders even when MO2 echoes code one");
            }
            foreach(var basis in new[]{"2","3","4","5"}) {
                f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance(basis,true));
                Check(!f.Evaluate().Ready&&!f.Evaluate(Sell()).Ready,
                    "explicit non-trade basis metadata still contradicts a bound request and prevents account orders");
            }
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true));
            f.Data.ObserveCash(new AccountCashSnapshot{Cash100Orderable=0,ReceivedUtc=Now},Now.AddMilliseconds(-100));
            Check(!f.Evaluate().Ready&&f.Evaluate(Sell()).Ready,
                "request-bound unknown metadata does not bypass actual buying power or unnecessarily require cash for a verified sale");
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("0",true));
            f.Data.ObserveCapacity(new AccountOrderCapacity{Code="000660",LimitPrice=10000,Ratio=100,ReceivedUtc=Now},Now.AddMilliseconds(-100));
            Check(!f.Evaluate().Ready&&f.Evaluate(Sell()).Ready,
                "request-bound holdings cannot expand the broker's zero permitted purchase quantity");
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>io.Fields[11]="1"));
            Check(!f.Evaluate().Ready&&!f.Evaluate(Sell()).Ready,
                "request context cannot hide unmatched actual unfilled quantities from order reconciliation");
            foreach(var field in new[]{3,22}) {
                f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>io.Fields[field]=""));
                Check(!f.Evaluate(Sell()).Ready,"request-bound unknown MO2 cannot waive ordinary cash-kind or no-loan source checks");
            }
            foreach(var field in new[]{4,10,11}) {
                f=new Fixture();f.Data.Invalidate(Now.AddMilliseconds(-500));bool rejected=false;
                try{f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>io.Fields[field]=field==4?"-1":field==10?"3":"malformed"));}
                catch(InvalidOperationException){rejected=true;}
                Check(rejected&&!f.Evaluate().Ready&&!f.Evaluate(Sell()).Ready,
                    "malformed required position, possible or unfilled quantity never becomes accepted readiness evidence");
            }

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
            TerminalCheckpointChecks();
            OrdinarySourceChecks();
            return checks;
        }
        static void TerminalCheckpointChecks() {
            var f=new Fixture();string key;
            using(var hash=SHA256.Create())key=string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes("ShinhanNativeTrader/real-account/v1/12345678901"))
                .Select(value=>value.ToString("x2")));
            f.Data=new BrokerReadiness(key,Fingerprint);f.Data.SetAuthorization(true);f.Data.SetAccountStream(true,Now.AddSeconds(-10));f.Refresh(Now);
            var order=Working(10);order.Kind=LiveOrderKind.Sell;order.TradeKind=LiveOrderKind.Sell;order.Status=LiveOrderStatus.Filled;order.RequestId=71;
            var fields=new[]{"0","11","0","0","0","synthetic","0","0"};
            order.ResponseEvidence=LiveOrderResponseParser.Read(71,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
            order.FillCheckpoint=new BrokerFillCheckpoint{AccountFingerprint=Fingerprint,TradingDate="20261112",BrokerOrderNumber="11",Code=order.Code,
                QueryName="SABA231Q1",ProductCode="01",CreditCode="00",SideCode="1",ConfirmationCode="0",TradeKind=LiveOrderKind.Sell,
                OrderQuantity=10,FilledQuantity=10,LimitPrice=10000,AverageFillPrice=10000,PreviousOrderUpdatedUtc=Now.AddSeconds(-10),
                PreviousBrokerChangeUtc=Now.AddSeconds(-11),RequestStartedUtc=Now.AddSeconds(-8),ReceivedUtc=Now.AddSeconds(-7),ObservedUtc=Now.AddSeconds(-6)};
            f.Journal.Orders.Add(order);f.Journal.LastBrokerChangeUtc=Now.AddSeconds(-6);
            var snapshot=new BrokerOrdersSnapshot{TradingDate="20261112"};
            snapshot.Orders.Add(new BrokerOrderRow{OrderNumber="00011",Code=order.Code,SideCode="1",ProductCode="01",CreditCode="00",ConfirmationCode="0",
                Quantity=10,LimitPrice=10000,FilledQuantity=10,OpenQuantity=0,AverageFillPrice=10000});
            snapshot.BindRequestScope(Now.AddMilliseconds(-200),Now.AddMilliseconds(-100),true,"12345678901");
            f.Data.ObserveOrders(snapshot,snapshot.RequestStartedUtc);
            var result=f.Evaluate();
            Check(result.Ready&&result.Checks.FillsReconciled&&result.Checks.RecoveryVerified&&f.Journal.Executions.Count==0,
                "a validated terminal broker checkpoint can explain its aggregate total without inventing execution IDs");
            var subset=new LiveExecutionRecord{OrderId=order.Id,BrokerOrderNumber="11",TradingDate="20261112",ExecutionId="ACTUAL-AA1",Quantity=3,Price=10000,ExecutedUtc=Now.AddSeconds(-6)};
            f.Journal.Executions.Add(subset);
            Check(f.Evaluate().Ready,"real execution subset is not added a second time to the terminal aggregate total");
            f.Journal.Executions.Add(subset);Check(!f.Evaluate().Ready,"duplicate real execution IDs still block a checkpoint-backed account");f.Journal.Executions.RemoveAt(1);
            subset.Price=9999;Check(!f.Evaluate().Ready,"a stored execution outside the sell limit cannot hide behind an aggregate checkpoint");
            subset.Price=10001;Check(!f.Evaluate().Ready,"known execution value cannot imply below-limit remaining checkpoint value");subset.Price=10000;
            f.Data.Invalidate(Now.AddMilliseconds(-90));
            Check(!f.Evaluate().Ready,"terminal checkpoint does not exempt current cash holdings or orders from new invalidation barriers");
            f.Data=new BrokerReadiness(key,Fingerprint);f.Data.SetAuthorization(true);f.Data.SetAccountStream(true,Now.AddSeconds(-10));f.Refresh(Now);
            snapshot.BindRequestScope(Now.AddMilliseconds(-200),Now.AddMilliseconds(-100),true,"12345678902");f.Data.ObserveOrders(snapshot,snapshot.RequestStartedUtc);
            Check(!f.Evaluate().Ready,"a fresh scoped different-account snapshot cannot validate an existing checkpoint");
            snapshot.BindRequestScope(Now.AddMilliseconds(-80),Now.AddMilliseconds(-40),true,"12345678901");
            f.Data.ObserveOrders(snapshot,Now.AddMilliseconds(-60));
            Check(!f.Evaluate().Ready,"a newer method argument cannot re-stamp a differently sealed request proof");
            snapshot.BindRequestScope(Now.AddMilliseconds(-30),Now.AddMilliseconds(-20),true,"12345678901");
            snapshot.Orders[0].AverageFillPrice=10001;f.Data.ObserveOrders(snapshot,snapshot.RequestStartedUtc);
            Check(!f.Evaluate().Ready,"a changed broker cumulative average invalidates the old terminal checkpoint");
            snapshot.Orders[0].AverageFillPrice=10000;
            Action observeFresh=()=> {
                f.Data=new BrokerReadiness(key,Fingerprint);f.Data.SetAuthorization(true);f.Data.SetAccountStream(true,Now.AddSeconds(-10));f.Refresh(Now);
                snapshot.BindRequestScope(Now.AddMilliseconds(-200),Now.AddMilliseconds(-100),true,"12345678901");
                f.Data.ObserveOrders(snapshot,snapshot.RequestStartedUtc);
            };
            foreach(var basis in new[]{BrokerFillConfirmationBasis.NormalCode,BrokerFillConfirmationBasis.IndependentFullFillWithoutConfirmation}) {
                order.FillCheckpoint.ConfirmationBasis=basis;order.FillCheckpoint.ConfirmationCode=basis==BrokerFillConfirmationBasis.NormalCode?"0":"";
                foreach(var code in new[]{"0",""}) {
                    snapshot.Orders[0].ConfirmationCode=code;observeFresh();
                    Check(f.Evaluate().Ready,"fresh exact full-fill row validates its independent checkpoint while retaining separate confirmation provenance");
                    foreach(var mutation in new Action<BrokerOrderRow>[] {
                        row=>row.ConfirmationCode="1",row=>row.ConfirmationCode="2",row=>row.ConfirmationCode="3",row=>row.ConfirmationCode="00",
                        row=>row.ConfirmationCode="9",row=>row.RejectCode="R001",row=>row.RejectName="거부",row=>row.StatusName="거부"
                    }) {
                        var row=snapshot.Orders[0];mutation(row);observeFresh();
                        row.ConfirmationCode=code;row.RejectCode=null;row.RejectName=null;row.StatusName=null;
                        Check(!f.Evaluate().Ready,"defensively copied conflicting confirmation or rejection metadata invalidates either checkpoint provenance");
                    }
                }
            }
            var point=order.FillCheckpoint;point.ConfirmationBasis=BrokerFillConfirmationBasis.ModernAcceptedStateWithFullFill;
            point.QueryProfile=BrokerOrderQueryProfile.ModernOrderState54;point.QuerySchemaSha256=new string('a',64);point.QuerySerializationVersion=0x0208;
            point.ConfirmationCode="1";point.StatusName="접수";point.RejectCodeShape="Blank";point.RejectNameShape="Blank";
            point.OrderMethodCode="1";point.SorOrderNumber="0";point.SorOriginalOrderNumber="0";
            Action resetModern=()=>snapshot.Orders[0]=new BrokerOrderRow{OrderNumber="00011",Code=order.Code,SideCode="1",ProductCode="01",CreditCode="00",
                Quantity=10,LimitPrice=10000,FilledQuantity=10,OpenQuantity=0,AverageFillPrice=10000,
                QueryProfile=BrokerOrderQueryProfile.ModernOrderState54,QuerySchemaSha256=new string('a',64),ConfirmationCode="1",StatusName="접수",
                OrderMethodCode="1",SorOrderNumber="0",SorOriginalOrderNumber="0"};
            resetModern();observeFresh();
            Check(f.Evaluate().Ready&&f.Evaluate().Checks.FillsReconciled&&f.Journal.Executions.Count==1,
                "current matching modern schema and receipt status validate the distinct full-fill checkpoint without adding executions");
            foreach(var mutation in new Action<BrokerOrderRow>[] {
                row=>row.QueryProfile=BrokerOrderQueryProfile.Unverified,row=>row.QuerySchemaSha256=null,row=>row.QuerySchemaSha256=new string('b',64),
                row=>row.StatusName="확인",row=>row.StatusName="거부",row=>row.ConfirmationCode="",row=>row.ConfirmationCode="2",row=>row.ConfirmationCode="3",
                row=>row.RejectCode="R001",row=>row.RejectName="거부",row=>row.OrderMethodCode="2",row=>row.SorOrderNumber="11",row=>row.SorOriginalOrderNumber="12"
            }) {
                resetModern();mutation(snapshot.Orders[0]);observeFresh();resetModern();
                Check(!f.Evaluate().Ready,"a changed source digest, receipt interpretation or alternate-venue metadata invalidates modern checkpoint readiness until reverified");
            }
            resetModern();snapshot.Orders[0].ConfirmationCode="0";observeFresh();
            Check(f.Evaluate().Ready&&point.ConfirmationCode=="1"&&point.ConfirmationBasis==BrokerFillConfirmationBasis.ModernAcceptedStateWithFullFill,
                "fresh explicit normal confirmation from the same recognized modern definition validates rather than rewrites the preserved received-state checkpoint");
            foreach(var mutation in new Action<BrokerOrderRow>[] {
                row=>row.QueryProfile=BrokerOrderQueryProfile.Unverified,row=>row.QuerySchemaSha256=new string('b',64),row=>row.RejectCode="R001",row=>row.RejectName="거부"
            }) {
                resetModern();snapshot.Orders[0].ConfirmationCode="0";mutation(snapshot.Orders[0]);observeFresh();
                Check(!f.Evaluate().Ready&&point.ConfirmationCode=="1",
                    "explicit normal code cannot exempt current schema provenance or rejection details while preserving the original checkpoint");
            }
            resetModern();snapshot.Orders[0].OrderMethodCode="";snapshot.Orders[0].SorOrderNumber="";snapshot.Orders[0].SorOriginalOrderNumber="";observeFresh();
            Check(f.Evaluate().Ready,"missing venue echoes never replace the preserved accepted KRX identity or contradict an existing compatible checkpoint");
            point.QuerySchemaSha256=new string('b',64);
            Check(!f.Evaluate().Ready,"a syntactically valid but different persisted schema digest cannot match the actual current scoped broker row");
            point.QuerySchemaSha256=new string('a',64);
        }
        static void OrdinarySourceChecks() {
            var trade=ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="";});
            var f=new Fixture();f.Data.ObserveTradeBalance(trade);
            Check(!f.Evaluate(Sell(2)).Ready&&f.Evaluate().Ready,"missing row cash metadata blocks sales without changing existing buy exposure checks");
            var ordinary=OrdinaryBalance();f.Data.ObserveOrdinaryBalance(ordinary);
            var ready=f.Evaluate(Sell(2));
            Check(ready.Ready&&ready.Checks.SellableQuantityVerified&&ready.Checks.SellableQuantity==2,"parser-produced bound ordinary subset plus fresh trade quantities establishes actual sellable shares");
            ordinary.RequestOrdinaryOnlyVerified=false;ordinary.Holdings[0].Quantity=0;trade.Holdings[0].Quantity=0;
            Check(f.Evaluate(Sell(2)).Ready,"caller mutation cannot change accepted copied ordinary or trade evidence");
            foreach(var configure in new Action<HoldingsResponse>[] {
                io=>io.Fields[0]="A000660",io=>{io.Fields[4]="1";io.Fields[10]="1";},io=>io.Fields[10]="1",io=>io.Fields[11]="1",io=>io.Fields[12]="69999",
                io=>io.Fields[3]="유통융자",io=>io.Fields[22]="1",io=>io.Fields[22]="bad",io=>io.Fields[2]="3",io=>io.Fields[2]="4"
            }) {
                f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="";}));
                f.Data.ObserveOrdinaryBalance(OrdinaryBalance(true,configure));
                Check(!f.Evaluate(Sell()).Ready,"mismatched or conflicting ordinary selection does not certify a sale");
            }
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="";}));
            f.Data.ObserveOrdinaryBalance(OrdinaryBalance(false));
            Check(!f.Evaluate(Sell()).Ready,"rows from a request without verified ordinary-only scope never certify sellable quantity");
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",false,io=>{io.Fields[3]="";io.Fields[22]="";}));
            f.Data.ObserveOrdinaryBalance(OrdinaryBalance());
            Check(!f.Evaluate(Sell()).Ready,"ordinary selection cannot substitute for trusted trade-basis quantity");
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="";}));
            ordinary=OrdinaryBalance();ordinary.RequestStartedUtc=Now.AddSeconds(-7);ordinary.ReceivedUtc=Now.AddSeconds(-6);
            f.Data.ObserveOrdinaryBalance(ordinary);
            Check(!f.Evaluate(Sell()).Ready,"ordinary proof older than five seconds cannot certify an order even when display proof would still be recent");
            f=new Fixture();f.Data.ObserveOrdinaryBalance(OrdinaryBalance());
            f.Data.Invalidate(Now.AddMilliseconds(-150));
            trade=ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="";});trade.RequestStartedUtc=Now.AddMilliseconds(-100);trade.ReceivedUtc=Now.AddMilliseconds(-50);
            f.Data.ObserveTradeBalance(trade);
            Check(!f.Evaluate(Sell()).Checks.SellableQuantityVerified,"an account change after the ordinary request requires a new subset rather than reusing old classification");
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="1";}));
            f.Data.ObserveOrdinaryBalance(OrdinaryBalance());
            Check(!f.Evaluate(Sell()).Ready,"positive financing amount on the trade source is not overwritten by an ordinary subset");
            f=new Fixture();f.Data.ObserveTradeBalance(ParsedBalance("",true,io=>{io.Fields[3]="";io.Fields[22]="";}));
            f.Data.ObserveOrdinaryBalance(OrdinaryBalance());ready=f.Evaluate(Sell());
            Check(ready.Ready&&ready.EvidenceStillCurrent(),"ordinary proof participates in the normal current-evidence token");
            ordinary=OrdinaryBalance();ordinary.RequestStartedUtc=Now;ordinary.ReceivedUtc=Now.AddMilliseconds(50);f.Data.ObserveOrdinaryBalance(ordinary);
            Check(!ready.EvidenceStillCurrent(),"new ordinary evidence invalidates a previously built order proof before send");
        }
    }
}
