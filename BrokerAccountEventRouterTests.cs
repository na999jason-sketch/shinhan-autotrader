using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace ShinhanNativeTrader {
    public static class BrokerAccountEventRouterTests {
        sealed class Fixture : IDisposable {
            readonly string directory=Path.Combine(Path.GetTempPath(),"indi-aa-router-"+Guid.NewGuid().ToString("N"));
            public readonly LiveOrderJournal Journal;
            public readonly BrokerAccountEventRouter Router;
            public int Invalidations,Changes,Unmatched,Unknown;
            public string LegacyPath {get{return Path.Combine(directory,"legacy.xml");}}
            public Fixture(bool date=true) {
                Directory.CreateDirectory(directory);Journal=new LiveOrderJournal(Path.Combine(directory,"orders.xml"));Journal.BindAccount("12345678901");
                Router=new BrokerAccountEventRouter(Journal,(utc,reason)=>Invalidations++);
                Router.Changed+=value=>Changes++;Router.Unmatched+=reason=>Unmatched++;Router.Unknown+=reason=>Unknown++;
                if(date)Router.SetBrokerDate("20261112",Utc(-1));
            }
            public LiveOrderRecord Pending(string key="entry",string code="005930",LiveOrderKind kind=LiveOrderKind.Buy,string original=null,int quantity=10,decimal price=10000m) {
                var order=Journal.Prepare(new LiveOrderIntent{ClientKey=key,Code=code,Kind=kind,OriginalBrokerOrderNumber=original,Quantity=quantity,LimitPrice=price},Utc());
                Journal.MarkDispatching(order.Id,Utc());Journal.MarkRequestAccepted(order.Id,7,Utc());return Find(order.Id);
            }
            public LiveOrderRecord Acknowledge(LiveOrderRecord order,string number="00123") {
                Journal.RecordAcknowledgement(order.Id,7,number,false,Utc(1));return Find(order.Id);
            }
            public LiveOrderRecord TerminalCheckpoint(bool knownIndividual=false,LiveOrderKind kind=LiveOrderKind.Buy) {
                var pending=Pending(kind:kind);
                var fields=new[]{"0","00123","0","","0","accepted","0","0"};
                var accepted=LiveOrderResponseParser.Read(7,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
                Journal.RecordAcknowledgement(pending.Id,7,accepted.BrokerOrderNumber,false,Utc(1),accepted);
                decimal average=kind==LiveOrderKind.Buy?9900:10100;
                if(knownIndividual)Journal.RecordExecution("00123","known-fill",2,average,Utc(2));
                Journal.MarkUnknown(pending.Id,Utc(3));
                var evidence=new BrokerTerminalFillEvidence{AccountFingerprint=Journal.Snapshot.AccountFingerprint,TradingDate="20261112",QueryName="SABA231Q1",
                    RequestStartedUtc=Utc(4),ReceivedUtc=Utc(5),RequestScopeVerified=true,MatchingOrderCount=1,
                    Row=new BrokerOrderRow{OrderNumber="000123",OriginalOrderNumber="",Code="005930",SideCode=kind==LiveOrderKind.Buy?"2":"1",
                        ProductCode="01",CreditCode="00",AmendCancelCode="0",ConfirmationCode="0",Quantity=10,LimitPrice=10000,
                        FilledQuantity=10,AverageFillPrice=average,OpenQuantity=0,ConfirmedQuantity=0,AutoCancelledQuantity=0}};
                if(!Journal.ReconcileKnownTerminalFill(pending.Id,evidence,Utc(5)))throw new InvalidOperationException("Synthetic terminal recovery did not apply.");
                return Find(pending.Id);
            }
            public LiveOrderRecord Find(string id){return Journal.Snapshot.Orders.Single(o=>o.Id==id);}
            public void Dispose(){Journal.Dispose();Directory.Delete(directory,true);}
        }
        static int checks;
        static DateTime Utc(int seconds=0){return new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc).AddSeconds(seconds).AddTicks(450);}
        static void Check(bool result,string description){checks++;if(!result)throw new Exception("BrokerAccountEventRouterTests: "+description);}
        static CashOrderEvent Event(string number="00123",string process="03",string execution="F1",int fill=2,int cumulative=2,int seconds=2) {
            return new CashOrderEvent{ProcessingCode=process,Code="005930",BrokerOrderNumber=number,OriginalBrokerOrderNumber="",
                ExecutionId=execution,Clock="091530",ProductCode="01",SideCode="02",TradeTypeCode="02",OrderStatusCode=process=="04"||process=="05"?"02":process=="09"||process=="19"||process=="98"||process=="99"?"03":"01",OriginalResultCode="0",
                OrderQuantity=10,CumulativeFilledQuantity=cumulative,IndividualFillQuantity=fill,ConfirmedQuantity=0,OpenQuantity=10-cumulative,
                OrderPrice=10000,IndividualFillPrice=9900,ReceivedUtc=Utc(seconds)};
        }
        static CashOrderEvent Confirmation(string number,string original,string process,int quantity,int confirmed,decimal price=10000m) {
            var result=Event(number,process);result.OriginalBrokerOrderNumber=original;result.OrderQuantity=quantity;result.ConfirmedQuantity=confirmed;
            result.IndividualFillQuantity=0;result.CumulativeFilledQuantity=0;result.OpenQuantity=quantity;result.OrderPrice=price;return result;
        }
        public static int Run() {
            checks=0;
            using(var fixture=new Fixture(false)) {
                fixture.Router.ObserveAA(Event());
                Check(fixture.Unmatched==1&&fixture.Unknown==0&&fixture.Invalidations==1,"account subscription before initial broker date refreshes a manual event without blocking read-only use");
            }
            using(var fixture=new Fixture(false)) {
                var order=fixture.Acknowledge(fixture.Pending());fixture.Router.ObserveAA(Event());
                Check(fixture.Unknown==1&&fixture.Find(order.Id).FilledQuantity==0,"AA without actual broker date cannot supply executions");
                fixture.Router.SetBrokerDate("20261112",Utc(1));fixture.Router.ObserveAA(Event());
                Check(fixture.Find(order.Id).FilledQuantity==2,"actual SABA609 date enables matching same-day broker evidence");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Pending();var value=Event();fixture.Router.ObserveAA(value);value.IndividualFillQuantity=9;
                Check(fixture.Router.BufferedCount==1&&fixture.Find(order.Id).FilledQuantity==0,"AA before TR ACK waits without guessing an order by symbol/time");
                fixture.Acknowledge(order);fixture.Router.DrainOnGatewayOrderChanged();
                Check(fixture.Router.BufferedCount==0&&fixture.Find(order.Id).FilledQuantity==2&&fixture.Journal.Snapshot.Executions.Single().Quantity==2,"positive exact ACK drains a copied per-fill event");
                Check(fixture.Journal.Snapshot.Executions.Single().ExecutedUtc==Utc(2),"journal stores receipt observation time rather than rounding broker HHmmss before creation");
                fixture.Router.ObserveAA(Event());fixture.Router.DrainOnGatewayOrderChanged();
                Check(fixture.Find(order.Id).FilledQuantity==2&&fixture.Journal.Snapshot.Executions.Count==1,"duplicate execution ID cannot create an extra fill");
                Check(fixture.Invalidations==1&&fixture.Changes==1,"identical known execution duplicates neither mutate orders nor starve fresh account preflight snapshots");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Pending();for(int i=0;i<20;i++)fixture.Router.ObserveAA(Event());
                Check(fixture.Router.BufferedCount==1&&fixture.Find(order.Id).Status==LiveOrderStatus.AwaitingAcknowledgement,"identical early execution duplicates do not exhaust the bounded queue");
                fixture.Acknowledge(order,"123");fixture.Router.DrainOnGatewayOrderChanged();
                Check(fixture.Find(order.Id).FilledQuantity==2&&fixture.Journal.Snapshot.Executions.Single().BrokerOrderNumber=="123","numeric broker ID padding is formatting only and journal retains its acknowledged raw ID");
            }
            using(var fixture=new Fixture()) {
                var first=fixture.Acknowledge(fixture.Pending(),"123");fixture.Journal.RecordExecution("123","original-fill",10,9900,Utc(2));
                var second=fixture.Pending("second");bool rejected=false;
                try{fixture.Acknowledge(second,"000123");}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&fixture.Find(first.Id).FilledQuantity==10&&string.IsNullOrEmpty(fixture.Find(second.Id).BrokerOrderNumber),"canonical duplicate broker ACK is refused before an ambiguous journal can form");
                var legacy=fixture.Journal.Snapshot;var conflicting=legacy.Orders.Single(o=>o.Id==second.Id);conflicting.BrokerOrderNumber="000123";conflicting.Status=LiveOrderStatus.Working;
                using(var file=File.Create(fixture.LegacyPath))new XmlSerializer(typeof(LiveOrderJournalState)).Serialize(file,legacy);
                rejected=false;try{using(var ambiguous=new LiveOrderJournal(fixture.LegacyPath)){} }catch(InvalidDataException){rejected=true;}
                Check(rejected,"legacy same-day canonical duplicate identifiers are blocked on restart instead of selecting one order");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Pending();fixture.Router.ObserveAA(Event("00999"));fixture.Acknowledge(order);fixture.Router.DrainOnGatewayOrderChanged();
                Check(fixture.Unmatched==1&&fixture.Find(order.Id).FilledQuantity==0&&fixture.Find(order.Id).Status==LiveOrderStatus.Working,"same-symbol external broker number never attaches to local submission");
                fixture.Router.ObserveAA(Event("00888"));
                Check(fixture.Unmatched==2&&fixture.Invalidations==2,"manual/unmatched events invalidate balances without invented fills or rejection");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());var value=Event();value.ExecutionId="";fixture.Router.ObserveAA(value);
                Check(fixture.Unknown==1&&fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Journal.Snapshot.Executions.Count==0,"missing unique execution ID blocks known order instead of inventing cumulative fills");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());var value=Event();value.SideCode="01";fixture.Router.ObserveAA(value);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Find(order.Id).FilledQuantity==0,"broker number with wrong trade direction cannot create execution");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());var value=Event();value.TradeTypeCode="03";fixture.Router.ObserveAA(value);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Find(order.Id).FilledQuantity==0,"credit trade cannot become a cash fill merely because account product and side match");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());var value=Event();value.Code="000660";fixture.Router.ObserveAA(value);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Find(order.Id).FilledQuantity==0,"exact broker number with a different symbol is inconsistent evidence");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());fixture.Router.ObserveAA(Event(execution:"F2",fill:2,cumulative:5,seconds:4));
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Find(order.Id).FilledQuantity==2,"a cumulative gap preserves only proven individual fill and blocks further trading");
                fixture.Router.ObserveAA(Event(execution:"F1",fill:3,cumulative:3,seconds:3));
                Check(fixture.Find(order.Id).FilledQuantity==5&&fixture.Find(order.Id).Status==LiveOrderStatus.Unknown,"late unique fill supplies actual missing evidence but does not self-certify recovery");
                Check(fixture.Journal.Snapshot.LastBrokerChangeUtc==Utc(4)&&fixture.Find(order.Id).UpdatedUtc==Utc(4),"out-of-order receipts cannot regress mutation/observation timestamps");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());fixture.Router.ObserveAA(Event(execution:"000001"));fixture.Router.ObserveAA(Event(execution:"1",seconds:3));
                Check(fixture.Find(order.Id).FilledQuantity==2&&fixture.Journal.Snapshot.Executions.Single().ExecutionId=="000001"&&fixture.Find(order.Id).Status==LiveOrderStatus.Unknown,"execution IDs remain exact and overlapping cumulative volume prevents a formatted duplicate from adding another delta");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());fixture.Router.ObserveAA(Event(fill:10,cumulative:10));
                var contradictory=Event(fill:9,cumulative:10);fixture.Router.ObserveAA(contradictory);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Find(order.Id).FilledQuantity==10,"contradictory duplicate broker evidence reopens a terminal order as Unknown without changing actual fills");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());var echo=Event();echo.OriginalResultCode="1";echo.OrderQuantity=100;
                fixture.Router.ObserveAA(echo);
                Check(fixture.Find(order.Id).FilledQuantity==0&&fixture.Find(order.Id).Status==LiveOrderStatus.Working,"original-role echo cannot double count or confirm a child command");
            }
            using(var fixture=new Fixture()) {
                var original=fixture.Acknowledge(fixture.Pending());fixture.Router.ObserveAA(Event());
                var child=fixture.Acknowledge(fixture.Pending("cancel",kind:LiveOrderKind.Cancel,original:"00123",quantity:8),"00124");
                var echo=Confirmation("00123","00123","05",8,3);echo.OriginalResultCode="1";fixture.Router.ObserveAA(echo);
                Check(fixture.Find(original.Id).CancelledQuantity==0&&fixture.Find(child.Id).Status==LiveOrderStatus.AwaitingConfirmation,"original-role cancel echo cannot confirm the original as a child cancellation");
                var confirmation=Confirmation("00124","00123","05",8,3);fixture.Router.ObserveAA(confirmation);
                Check(fixture.Find(original.Id).FilledQuantity==2&&fixture.Find(original.Id).CancelledQuantity==3&&fixture.Find(original.Id).Status==LiveOrderStatus.PartiallyFilled,"partial cancel confirmation only releases confirmed remaining quantity");
                Check(fixture.Find(child.Id).Status==LiveOrderStatus.Cancelled&&fixture.Journal.ReservedBuyAmount==50000,"cancel child completion preserves still-working original exposure");
                fixture.Router.ObserveAA(confirmation);
                Check(fixture.Find(original.Id).CancelledQuantity==3&&fixture.Unknown==0,"duplicate cancel confirmation is idempotent");
            }
            using(var fixture=new Fixture()) {
                var original=fixture.Acknowledge(fixture.Pending());var child=fixture.Acknowledge(fixture.Pending("amend",kind:LiveOrderKind.Amend,original:"00123",price:10100),"00124");
                var confirmation=Confirmation("00124","00123","04",10,10,10100);fixture.Router.ObserveAA(confirmation);
                Check(fixture.Find(original.Id).Status==LiveOrderStatus.Replaced&&fixture.Find(child.Id).Status==LiveOrderStatus.Working,"exact full amendment confirmation activates only its known replacement");
                var fill=Event("00124",execution:"F2");fill.OriginalBrokerOrderNumber="00123";fill.OrderPrice=10100;fill.IndividualFillPrice=10050;fixture.Router.ObserveAA(fill);
                Check(fixture.Find(child.Id).FilledQuantity==2&&fixture.Find(original.Id).FilledQuantity==0,"replacement fills remain distinct from original order");
            }
            using(var fixture=new Fixture()) {
                var original=fixture.Acknowledge(fixture.Pending());var child=fixture.Acknowledge(fixture.Pending("amend",kind:LiveOrderKind.Amend,original:"00123",price:10100),"00124");
                fixture.Router.ObserveAA(Confirmation("00124","00123","04",10,3,10100));
                Check(fixture.Find(original.Id).Status==LiveOrderStatus.Unknown&&fixture.Find(child.Id).Status==LiveOrderStatus.Unknown,"unsupported partial amendment cannot replace the full original order");
            }
            using(var fixture=new Fixture()) {
                var original=fixture.Acknowledge(fixture.Pending());var child=fixture.Acknowledge(fixture.Pending("cancel",kind:LiveOrderKind.Cancel,original:"00123"),"00124");
                fixture.Router.ObserveAA(Confirmation("00124","00123","98",10,0));
                Check(fixture.Find(child.Id).Status==LiveOrderStatus.Rejected&&fixture.Find(original.Id).Status==LiveOrderStatus.Working&&fixture.Journal.ReservedBuyAmount==100000,"explicit cancel rejection preserves original order and its reserved funds");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());fixture.Router.ObserveAA(Event(process:"09",fill:0,cumulative:0));
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Rejected&&fixture.Journal.Snapshot.Executions.Count==0,"explicit broker new-order rejection is distinct from parse failure");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Pending();
                for(int i=0;i<17;i++)fixture.Router.ObserveAA(Event(execution:"F"+i));
                Check(fixture.Router.BufferedCount==0&&fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Unknown>0,"bounded pre-ACK buffer fails closed rather than losing executions and continuing");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Pending();fixture.Router.ObserveAA(Event());fixture.Router.ObserveAA(Event("00999",seconds:33));
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Router.BufferedCount==0,"expired pre-ACK evidence requires reconciliation rather than guessed matching");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.Acknowledge(fixture.Pending());fixture.Router.SetBrokerDate("20261113",Utc().AddDays(1));
                var value=Event();value.ReceivedUtc=Utc(2).AddDays(1);fixture.Router.ObserveAA(value);
                Check(fixture.Find(order.Id).FilledQuantity==0&&fixture.Unmatched==1,"a broker number from another confirmed trading day cannot attach to yesterday's order");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint();
                fixture.Router.ObserveAA(Event(fill:2,cumulative:2,seconds:6));
                var late=Event(execution:"later-fill",fill:3,cumulative:7,seconds:7);late.IndividualFillPrice=9980;
                fixture.Router.ObserveAA(late);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Filled&&fixture.Find(order.Id).FilledQuantity==10
                    &&fixture.Journal.Snapshot.Executions.Count==0,
                    "delayed individual ranges already covered by a terminal broker total cannot create fabricated executions or double quantity");
                Check(fixture.Unknown==0&&fixture.Invalidations==0&&fixture.Changes==0,
                    "covered historical fills with distinct valid prices do not reopen recovery or starve fresh account snapshots");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint(true);
                fixture.Router.ObserveAA(Event(execution:"known-fill",fill:2,cumulative:2,seconds:6));
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Filled&&fixture.Journal.Snapshot.Executions.Count==1,
                    "a real individual execution retained beneath a broker checkpoint remains an idempotent historical event");
                var conflict=Event(execution:"known-fill",fill:2,cumulative:2,seconds:7);conflict.IndividualFillPrice=9950;
                fixture.Router.ObserveAA(conflict);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Unknown==1&&fixture.Find(order.Id).FilledQuantity==10
                    &&fixture.Journal.Snapshot.Executions.Single().Price==9900,
                    "a conflicting known execution ID reopens checkpoint reconciliation without changing the authoritative total or earlier execution");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint();var conflict=Event(fill:2,cumulative:2,seconds:6);conflict.IndividualFillPrice=10001;
                fixture.Router.ObserveAA(conflict);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Journal.Snapshot.Executions.Count==0&&fixture.Unknown==1,
                    "a late buy execution above the submitted limit cannot be excused by an aggregate checkpoint");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint();var conflict=Event(fill:10,cumulative:10,seconds:6);conflict.IndividualFillPrice=9950;
                fixture.Router.ObserveAA(conflict);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Unknown==1&&fixture.Journal.Snapshot.Executions.Count==0,
                    "a full-quantity individual execution with a different total value contradicts the checkpoint even within the buy limit");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint();var conflict=Event(fill:2,cumulative:2,seconds:6);conflict.IndividualFillPrice=9000;
                fixture.Router.ObserveAA(conflict);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Unknown==1,
                    "an individually valid historical buy price cannot imply an impossible above-limit remainder under the broker total");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint(kind:LiveOrderKind.Sell);var late=Event(fill:2,cumulative:2,seconds:6);
                late.SideCode="01";late.TradeTypeCode="01";late.IndividualFillPrice=10150;fixture.Router.ObserveAA(late);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Filled&&fixture.Journal.Snapshot.Executions.Count==0,
                    "a delayed sell fill covered by the terminal total may have its own valid price distinct from the aggregate average");
                late=Event(execution:"below-limit",fill:2,cumulative:2,seconds:7);late.SideCode="01";late.TradeTypeCode="01";late.IndividualFillPrice=9990;
                fixture.Router.ObserveAA(late);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Unknown==1,
                    "a late sell execution below the submitted limit restores reconciliation instead of silently accepting inconsistent evidence");
            }
            using(var fixture=new Fixture()) {
                var order=fixture.TerminalCheckpoint();var conflict=Event(fill:2,cumulative:2,seconds:6);conflict.Code="000660";
                fixture.Router.ObserveAA(conflict);
                Check(fixture.Find(order.Id).Status==LiveOrderStatus.Unknown&&fixture.Journal.RecoveryRequired&&fixture.Unknown==1,
                    "a matching accepted broker ID with a contradictory symbol cannot retain a terminal checkpoint as reconciled");
            }
            return checks;
        }
    }
}
