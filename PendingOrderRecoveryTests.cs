using System;
using System.IO;
using System.Linq;

namespace ShinhanNativeTrader {
    // All orders, credentials and callbacks in these tests are synthetic. No
    // wire or ActiveX control is constructed by restart/cancellation recovery.
    public static class PendingOrderRecoveryTests {
        const string Account="12345678901";
        static int checks;
        static DateTime Utc(int seconds=0){return new DateTime(2026,11,16,3,0,0,DateTimeKind.Utc).AddSeconds(seconds);}
        static void Check(bool value,string label){checks++;if(!value)throw new Exception("PendingOrderRecoveryTests: "+label);}
        sealed class Fixture : IDisposable {
            public readonly string DirectoryPath,Path;
            public LiveOrderJournal Journal;
            public string OriginalId,CancelId;
            public Fixture() {
                DirectoryPath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"indi-pending-recovery-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DirectoryPath);Path=System.IO.Path.Combine(DirectoryPath,"journal.xml");
                Journal=new LiveOrderJournal(Path);Journal.BindAccount(Account);
                var original=Accept(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code="084695",Quantity=3,LimitPrice=13910,ClientKey="synthetic-original"},"000001234",0);
                OriginalId=original.Id;
            }
            public LiveOrderRecord Original {get{return Journal.Snapshot.Orders.Single(value=>value.Id==OriginalId);}}
            public LiveOrderRecord Child {get{return Journal.Snapshot.Orders.Single(value=>value.Id==CancelId);}}
            LiveOrderRecord Accept(LiveOrderIntent intent,string number,int time) {
                var order=Journal.Prepare(intent,Utc(time));Journal.MarkDispatching(order.Id,Utc(time));
                Journal.MarkRequestAccepted(order.Id,time+1,Utc(time));
                var slots=new[]{"0",number,"0","","0","synthetic ACK","0","0"};
                var response=LiveOrderResponseParser.Read(time+1,index=>slots[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
                Journal.RecordAcknowledgement(order.Id,time+1,number,false,Utc(time+1),response);return Journal.Snapshot.Orders.Single(value=>value.Id==order.Id);
            }
            public void Cancel() {
                CancelId=Accept(new LiveOrderIntent{Kind=LiveOrderKind.Cancel,Code=Original.Code,Quantity=3,LimitPrice=13910,
                    OriginalBrokerOrderNumber=Original.BrokerOrderNumber,ClientKey="timeout-cancel-"+OriginalId+"-1"},"000001235",10).Id;
            }
            public void Restart(){Journal.Dispose();Journal=new LiveOrderJournal(Path);}
            public void Dispose(){if(Journal!=null)Journal.Dispose();try{Directory.Delete(DirectoryPath,true);}catch(IOException){}}
        }
        static BrokerOrderRow Row(LiveOrderRecord order) {
            return new BrokerOrderRow{OrderNumber=order.BrokerOrderNumber.TrimStart('0'),OriginalOrderNumber=order.OriginalBrokerOrderNumber??"0",
                Code=order.Code,SideCode=order.TradeKind==LiveOrderKind.Buy?"2":"1",AmendCancelCode="",ProductCode="01",CreditCode="00",
                Quantity=order.Quantity,LimitPrice=order.LimitPrice,FilledQuantity=order.FilledQuantity,
                OpenQuantity=order.Kind==LiveOrderKind.Cancel?0:order.Quantity-order.FilledQuantity-order.CancelledQuantity,
                AverageFillPrice=order.FilledQuantity>0?13900:0,AutoCancelledQuantity=order.CancelledQuantity,
                QueryProfile=BrokerOrderQueryProfile.ModernOrderState54,QuerySchemaSha256=new string('a',64),
                OrderMethodCode="1",SorOrderNumber="0",SorOriginalOrderNumber="0",ConfirmationCode="1",StatusName="접수"};
        }
        static BrokerOrdersSnapshot Snapshot(Fixture value,bool includeChild=false,string account=Account) {
            var snapshot=new BrokerOrdersSnapshot{TradingDate="20261116"};snapshot.Orders.Add(Row(value.Original));
            if(includeChild)snapshot.Orders.Add(Row(value.Child));snapshot.BindRequestScope(Utc(20),Utc(21),true,account);return snapshot;
        }
        static void OpenRecovery() {
            using(var fixture=new Fixture()) {
                var created=fixture.Original.CreatedUtc;fixture.Restart();string reason;
                Check(fixture.Journal.RecoveryRequired,"restart first preserves the working order as unknown");
                Check(fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,Snapshot(fixture),Account,Utc(22),out reason),"preserved ACK and fresh exact normal query restore open order despite no snapshot completeness guarantee");
                Check(fixture.Original.Status==LiveOrderStatus.Working&&!fixture.Journal.RecoveryRequired,"known unfilled buy returns to working");
                Check(fixture.Original.CreatedUtc==created&&fixture.Journal.ReservedBuyAmount==41730,"recovery preserves original timeout age and full reservation");
                Check(fixture.Journal.Snapshot.Executions.Count==0,"open recovery creates no execution");
                fixture.Cancel();Check(fixture.Child.Status==LiveOrderStatus.AwaitingConfirmation,"recovered original permits one separately acknowledged cancellation");
            }
            Action<BrokerOrderRow>[] conflicts={
                row=>row.Code="005930",row=>row.SideCode="1",row=>row.ProductCode="02",row=>row.CreditCode="01",
                row=>row.Quantity=4,row=>row.LimitPrice=13900,row=>row.FilledQuantity=1,row=>row.OpenQuantity=2,
                row=>row.ConfirmedQuantity=1,row=>row.AutoCancelledQuantity=1,row=>row.OriginalOrderNumber="999",
                row=>row.ConfirmationCode="",row=>row.ConfirmationCode="2",row=>row.StatusName="거부",
                row=>row.RejectCode="99",row=>row.OrderMethodCode="0",row=>row.SorOrderNumber="999",
                row=>row.QuerySchemaSha256="bad",row=>row.AverageFillPrice=13900
            };
            foreach(var conflict in conflicts)using(var fixture=new Fixture()) {
                fixture.Restart();var snapshot=Snapshot(fixture);conflict(snapshot.Orders[0]);string reason;
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(22),out reason),"conflicting open row remains unknown");
                Check(fixture.Journal.RecoveryRequired&&fixture.Journal.ReservedBuyAmount==41730,"conflicting open evidence releases no reservation");
            }
            using(var fixture=new Fixture()) {
                fixture.Restart();string reason;var snapshot=Snapshot(fixture,account:"12345678902");
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(22),out reason),"another account request cannot recover the journal");
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,"12345678902",Utc(22),out reason),"matching request account still must match journal fingerprint");
                snapshot=Snapshot(fixture);Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(30),out reason),"stale known open snapshot remains blocked");
                snapshot=Snapshot(fixture);snapshot.TradingDate="20261117";
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(22),out reason),"changed date invalidates private request scope");
                snapshot=Snapshot(fixture);snapshot.Orders.Add(Row(fixture.Original));
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(22),out reason),"duplicate normalized broker identities remain ambiguous");
                snapshot=Snapshot(fixture);snapshot.RequestStartedUtc=Utc(0);
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(22),out reason),"tampered or pre-ACK request time cannot recover");
            }
            using(var fixture=new Fixture()) {
                fixture.Journal.RecordExecution(fixture.Original.BrokerOrderNumber,"synthetic-fill-1",1,13900,Utc(2));fixture.Restart();string reason;
                var wrong=Snapshot(fixture);wrong.Orders[0].FilledQuantity=2;wrong.Orders[0].OpenQuantity=1;
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,wrong,Account,Utc(22),out reason),"aggregate missing individual execution cannot recover partial order");
                wrong=Snapshot(fixture);wrong.Orders[0].AverageFillPrice=13890;
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,wrong,Account,Utc(22),out reason),"conflicting aggregate execution amount remains blocked");
                Check(fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,Snapshot(fixture),Account,Utc(22),out reason),"known individual partial fill is retained on exact recovery");
                Check(fixture.Original.Status==LiveOrderStatus.PartiallyFilled&&fixture.Original.FilledQuantity==1&&fixture.Journal.Snapshot.Executions.Count==1,"partial recovery never duplicates fills");
            }
        }
        static void CancellationRecovery() {
            using(var fixture=new Fixture()) {
                fixture.Cancel();fixture.Restart();string reason;var snapshot=Snapshot(fixture,true);
                Check(!fixture.Journal.TryReconcileKnownOpenOrder(fixture.OriginalId,snapshot,Account,Utc(22),out reason),"open recovery cannot ignore its active cancellation child");
                Check(fixture.Journal.TryRestoreKnownCancellation(fixture.CancelId,snapshot,Account,Utc(22),out reason),"exact accepted original and cancellation restore pending confirmation together");
                Check(fixture.Child.Status==LiveOrderStatus.AwaitingConfirmation&&fixture.Original.Status==LiveOrderStatus.Working&&!fixture.Journal.RecoveryRequired,"accepted cancel is restored only to awaiting confirmation");
                Check(fixture.Original.CancelledQuantity==0&&fixture.Journal.ReservedBuyAmount==41730,"restored cancellation ACK releases no money or volume");
                fixture.Journal.ConfirmCancellation(fixture.Original.BrokerOrderNumber,fixture.Child.BrokerOrderNumber,"AA05",3,Utc(23));
                fixture.Journal.ConfirmCancellation(fixture.Original.BrokerOrderNumber,fixture.Child.BrokerOrderNumber,"AA05",3,Utc(24));
                Check(fixture.Original.Status==LiveOrderStatus.Cancelled&&fixture.Original.CancelledQuantity==3&&fixture.Journal.ReservedBuyAmount==0,"actual cancellation confirmation releases remainder exactly once");
            }
            using(var fixture=new Fixture()) {
                fixture.Cancel();fixture.Journal.RecordExecution(fixture.Original.BrokerOrderNumber,"synthetic-race-fill",1,13900,Utc(12));fixture.Restart();string reason;
                Check(fixture.Journal.TryRestoreKnownCancellation(fixture.CancelId,Snapshot(fixture,true),Account,Utc(22),out reason),"fill arriving while cancellation pending is retained during restart recovery");
                Check(fixture.Journal.ReservedBuyAmount==27820&&fixture.Original.FilledQuantity==1,"only remaining two shares stay reserved after the real fill");
                fixture.Journal.ConfirmCancellation(fixture.Original.BrokerOrderNumber,fixture.Child.BrokerOrderNumber,"AA05",2,Utc(23));
                Check(fixture.Original.FilledQuantity==1&&fixture.Original.CancelledQuantity==2&&fixture.Journal.Snapshot.Executions.Count==1,"cancel confirms only unfilled remainder and preserves actual partial holding");
            }
            Action<BrokerOrdersSnapshot>[] conflicts={
                snapshot=>snapshot.Orders.RemoveAt(1),snapshot=>snapshot.Orders.RemoveAt(0),
                snapshot=>snapshot.Orders[1].OriginalOrderNumber="999",snapshot=>snapshot.Orders[1].OrderNumber="999",
                snapshot=>snapshot.Orders[1].ConfirmedQuantity=3,snapshot=>snapshot.Orders[0].AutoCancelledQuantity=3,
                snapshot=>snapshot.Orders[0].OpenQuantity=0,snapshot=>snapshot.Orders[1].OpenQuantity=3,
                snapshot=>snapshot.Orders[0].FilledQuantity=1,snapshot=>snapshot.Orders[1].ConfirmationCode="",
                snapshot=>snapshot.Orders[1].RejectCode="99",snapshot=>snapshot.Orders.Add(snapshot.Orders[1])
            };
            foreach(var conflict in conflicts)using(var fixture=new Fixture()) {
                fixture.Cancel();fixture.Restart();var snapshot=Snapshot(fixture,true);conflict(snapshot);string reason;
                Check(!fixture.Journal.TryRestoreKnownCancellation(fixture.CancelId,snapshot,Account,Utc(22),out reason),"ambiguous cancellation query cannot release or restore without evidence");
                Check(fixture.Journal.RecoveryRequired&&fixture.Original.CancelledQuantity==0&&fixture.Journal.ReservedBuyAmount==41730,"uncertain cancellation keeps recovery block and reservation");
            }
        }
        static void UnknownSubmission() {
            string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"indi-request-unknown-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try {
                string path=System.IO.Path.Combine(directory,"journal.xml");string id;
                using(var journal=new LiveOrderJournal(path)) {
                    journal.BindAccount(Account);var order=journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code="084695",Quantity=3,LimitPrice=13910,ClientKey="synthetic-unknown"},Utc());
                    id=order.Id;journal.MarkDispatching(id,Utc());journal.MarkRequestAccepted(id,1,Utc());
                }
                using(var journal=new LiveOrderJournal(path)) {
                    var snapshot=new BrokerOrdersSnapshot{TradingDate="20261116"};snapshot.Orders.Add(Row(new LiveOrderRecord{BrokerOrderNumber="1234",Code="084695",Kind=LiveOrderKind.Buy,TradeKind=LiveOrderKind.Buy,Quantity=3,LimitPrice=13910}));
                    snapshot.BindRequestScope(Utc(20),Utc(21),true,Account);string reason;
                    Check(!journal.TryReconcileKnownOpenOrder(id,snapshot,Account,Utc(22),out reason),"matching symbol and amount never attach an unknown request to a broker ID");
                    Check(journal.RecoveryRequired&&journal.Snapshot.Orders.Single().Status==LiveOrderStatus.Unknown,"unacknowledged submission remains blocked and is never replayed");
                }
                File.Delete(path);
                using(var journal=new LiveOrderJournal(path)) {
                    journal.BindAccount(Account);var order=journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code="084695",Quantity=3,LimitPrice=13910,ClientKey="synthetic-no-evidence"},Utc());
                    id=order.Id;journal.MarkDispatching(id,Utc());journal.MarkRequestAccepted(id,1,Utc());journal.RecordAcknowledgement(id,1,"1234",false,Utc(1));
                }
                using(var journal=new LiveOrderJournal(path)) {
                    var snapshot=new BrokerOrdersSnapshot{TradingDate="20261116"};snapshot.Orders.Add(Row(journal.Snapshot.Orders.Single()));snapshot.BindRequestScope(Utc(20),Utc(21),true,Account);string reason;
                    Check(!journal.TryReconcileKnownOpenOrder(id,snapshot,Account,Utc(22),out reason),"older positive number without retained accepted response stays blocked");
                }
            } finally {try{Directory.Delete(directory,true);}catch(IOException){}}
        }
        public static int Run(){checks=0;OpenRecovery();CancellationRecovery();UnknownSubmission();return checks;}
    }
}
