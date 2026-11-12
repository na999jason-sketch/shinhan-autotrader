using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace ShinhanNativeTrader {
    public static class LiveOrdersTests {
        sealed class FakeWire : ILiveOrderWire {
            public event Action<LiveOrderAcknowledgement> Acknowledged;
            public int Sends;public bool Disposed;
            public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc){Sends++;return 7;}
            public void Emit(LiveOrderAcknowledgement value){if(Acknowledged!=null)Acknowledged(value);}
            public void Dispose(){Disposed=true;}
        }
        static int checks;
        static void Check(bool value,string label){checks++;if(!value)throw new Exception("LiveOrdersTests: "+label);}
        static void Reject(Action action,string label){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,label);}
        static DateTime Utc(int seconds=0){return new DateTime(2026,11,12,1,0,0,DateTimeKind.Utc).AddSeconds(seconds);}
        static LiveOrderIntent Buy(string key="buy-a",string code="005930",int quantity=10,decimal price=10000){return new LiveOrderIntent{Kind=LiveOrderKind.Buy,ClientKey=key,Code=code,Quantity=quantity,LimitPrice=price};}
        static LiveOrderIntent Modify(LiveOrderKind kind,string key,string original,int qty=10,decimal price=10000){return new LiveOrderIntent{Kind=kind,ClientKey=key,Code="005930",OriginalBrokerOrderNumber=original,Quantity=qty,LimitPrice=price};}
        static LiveOrderPreflight Ready(DateTime utc){return new LiveOrderPreflight{AccountAuthorized=true,OrderableAmountVerified=true,SellableQuantityVerified=true,OrdersReconciled=true,FillsReconciled=true,RecoveryVerified=true,PriceRuleVerified=true,QuoteVerified=true,Cash100Orderable=1000000,CapacityAmount=1000000,OperatingLimit=1000000,CapacityQuantity=100,SellableQuantity=100,SnapshotUtc=utc,QuoteUtc=utc};}
        static LiveOrderRecord Accepted(LiveOrderJournal journal,LiveOrderIntent intent,string number,DateTime utc,int rid=7) {
            var order=journal.Prepare(intent,utc);journal.MarkDispatching(order.Id,utc);journal.MarkRequestAccepted(order.Id,rid,utc);journal.RecordAcknowledgement(order.Id,rid,number,false,utc);return journal.Snapshot.Orders.Single(o=>o.Id==order.Id);
        }
        public static int Run() {
            checks=0;
            var directory=Path.Combine(Path.GetTempPath(),"ShinhanLiveOrderTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try {
                var credentials=new LiveAccountCredentials("12345678901","Z!pw");
                var intent=Buy();var fields=LiveOrderProtocol.BuildFields(intent,credentials);
                Check(LiveOrderProtocol.QueryName(intent)=="SABA101U1","cash buy selects official new-order query");
                Check(fields[0]==credentials.Account&&fields[1]=="01"&&fields[2]==credentials.Password,"credentials are forwarded only in transient request fields");
                Check(fields[5]=="0"&&fields[6]=="00"&&fields[7]=="2"&&fields[8]=="A005930","buy is regular cash stock without credit/shorting");
                Check(fields[9]=="10"&&fields[10]=="10000"&&fields[11]=="1"&&fields[12]=="2","explicit quantity and regular-session limit price are used");
                Check(fields[16]==""&&fields[21]=="Y"&&!fields.ContainsKey(22),"new order requests result messages without bypassing validation flags");
                intent.Kind=LiveOrderKind.Sell;Check(LiveOrderProtocol.BuildFields(intent,credentials)[7]=="1","sell uses the official sell code");
                intent=Modify(LiveOrderKind.Amend,"amend-a","00000123");fields=LiveOrderProtocol.BuildFields(intent,credentials);
                Check(LiveOrderProtocol.QueryName(intent)=="SABA102U1"&&fields[7]=="3"&&fields[16]=="00000123","amend preserves original broker identifier including leading zeroes");
                Check(fields[14]==""&&!fields.ContainsKey(21),"amend does not invent new-order-only fields");
                intent.Kind=LiveOrderKind.Cancel;Check(LiveOrderProtocol.BuildFields(intent,credentials)[7]=="4","cancel uses the official cancellation operation");
                intent.Quantity=0;Reject(()=>LiveOrderProtocol.BuildFields(intent,credentials),"undocumented zero-quantity cancel-all is rejected");
                intent=Buy();intent.LimitPrice=0;Reject(()=>LiveOrderProtocol.BuildFields(intent,credentials),"market/zero-price orders are not silently inferred");
                intent=Buy();intent.LimitPrice=10000.5m;Reject(()=>LiveOrderProtocol.BuildFields(intent,credentials),"fractional KRW limit price is rejected");
                intent=Buy();intent.OriginalBrokerOrderNumber="123";Reject(()=>LiveOrderProtocol.BuildFields(intent,credentials),"new order cannot accidentally amend an original order");
                Reject(()=>new LiveAccountCredentials("123-456","SECRET"),"malformed account is rejected without exposing its value");
                Check(!LiveOrderProtocol.ValidBrokerNumber("0")&&!LiveOrderProtocol.ValidBrokerNumber("000000")&&!LiveOrderProtocol.ValidBrokerNumber("")&&!LiveOrderProtocol.ValidBrokerNumber("A123"),"blank, zero and nonnumeric acknowledgements are not accepted order identifiers");
                Check(LiveOrderProtocol.ValidBrokerNumber("00000123"),"positive broker identifier remains a string");

                intent=Buy();var ready=Ready(Utc());
                LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1));Check(true,"fresh authoritative preflight values validate independently of the closed production gate");
                ready.Cash100Orderable=1;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"insufficient actual cash100 orderable amount blocks buy");
                ready=Ready(Utc());ready.CapacityQuantity=9;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"broker orderable quantity blocks oversized buy");
                ready=Ready(Utc());ready.OperatingLimit=99999;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"configured operating limit does not substitute for actual cash");
                ready=Ready(Utc());ready.OperatingLimit=150000;
                Reject(()=>LiveOrderSafety.ValidatePreflight(Buy("second-stock","000660"),ready,Utc(),Utc(-1),100000),"fresh large broker capacity cannot spend operating funds reserved by a different stock");
                LiveOrderSafety.ValidatePreflight(Buy("remaining-budget","000660",5),ready,Utc(),Utc(-1),100000);
                Check(true,"exact remaining operating budget is admissible with authoritative capacity");
                Reject(()=>LiveOrderSafety.ValidatePreflight(Buy(),ready,Utc(),Utc(-1),-1),"negative local reserved exposure cannot inflate the operating limit");
                ready=Ready(Utc(-6));Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-10)),"stale balance/capacity query is rejected");
                ready=Ready(Utc());Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(1)),"balance snapshot preceding the latest broker mutation is rejected");
                ready=Ready(Utc());ready.QuoteUtc=Utc(1);Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"future quote timestamp is rejected");
                ready=Ready(Utc());ready.QuoteVerified=false;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"price-only snapshots cannot be used for order submission");
                ready=Ready(Utc());ready.FillsReconciled=false;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"order aggregates without execution identifiers do not establish fill reconciliation");
                intent.Kind=LiveOrderKind.Sell;ready=Ready(Utc());ready.SellableQuantity=9;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"sellable stock quantity is checked separately from holdings");
                intent=Modify(LiveOrderKind.Amend,"amend-check","00000123");ready=Ready(Utc());
                Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"amendment cannot bypass the original-side verification");
                ready.OriginalOrderVerified=true;ready.OriginalTradeKind=LiveOrderKind.Buy;ready.Cash100Orderable=1;
                Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"buy-side amendment still checks fresh orderable cash");
                ready=Ready(Utc());ready.OriginalOrderVerified=true;ready.OriginalTradeKind=LiveOrderKind.Sell;ready.SellableQuantity=1;
                Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"sell-side amendment still checks sellable quantity");
                Check(!LiveOrderSafety.ProductionContractVerified,"production integration cannot be enabled with a config/UI flag");
                Reject(LiveOrderSafety.EnsureAllowed,"unverified actual-money transport is locked by default");
                Reject(()=>new ComLiveOrderWire(new object()),"COM order wire requires an actual hosted native control");

                string path=Path.Combine(directory,"actual-orders.xml");string firstId;
                using(var journal=new LiveOrderJournal(path)) {
                    journal.BindAccount(credentials.Account);
                    Reject(()=>journal.BindAccount("10987654321"),"journal cannot be silently reused for another actual account");
                    using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire)) {
                        Reject(()=>gateway.Submit(Buy(),credentials,Ready(Utc()),Utc()),"actual-order gateway checks the production gate before preparing/sending");
                        Check(wire.Sends==0&&journal.Snapshot.Orders.Count==0,"closed production gate cannot create fake orders or contact a transport");
                        wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="123"});Check(journal.Snapshot.Orders.Count==0,"unsolicited ack does not create a broker order");
                        gateway.Timeout(Utc());Check(!gateway.Busy,"idle timeout has no order side effects");
                    }
                    var order=journal.Prepare(Buy(),Utc());firstId=order.Id;
                    Check(order.Status==LiveOrderStatus.Prepared&&order.FilledQuantity==0&&order.TradingDate=="20261112","prepared intent is distinct from sent/accepted/filled evidence");
                    Check(journal.ReservedBuyAmount==100000,"unresolved buy exposure is derived from remaining quantity");
                    Reject(()=>journal.Prepare(Buy(),Utc()),"same client key is durably rejected");
                    Reject(()=>journal.Prepare(Buy("buy-b"),Utc()),"another unresolved order for the same stock is rejected");
                    journal.MarkDispatching(order.Id,Utc());Reject(()=>journal.MarkDispatching(order.Id,Utc()),"durable dispatch cannot be replayed");
                    journal.MarkRequestAccepted(order.Id,7,Utc());
                    Check(journal.Snapshot.Orders[0].Status==LiveOrderStatus.AwaitingAcknowledgement&&journal.Snapshot.Executions.Count==0,"accepted request ID is not an order acknowledgement or execution");
                    Reject(()=>journal.RecordAcknowledgement(order.Id,99,"00000123",false,Utc()),"mismatched acknowledgement request ID is rejected");
                    journal.RecordAcknowledgement(order.Id,7,"00000123",false,Utc());
                    Check(journal.Snapshot.Orders[0].Status==LiveOrderStatus.Working&&journal.Snapshot.Orders[0].FilledQuantity==0,"positive order number establishes acknowledgement only");
                    Check(journal.RecordExecution("00000123","F1",3,9900,Utc(10)),"authoritative unique execution updates actual filled quantity");
                    Check(journal.Snapshot.Orders[0].FilledQuantity==3&&journal.Snapshot.Orders[0].Status==LiveOrderStatus.PartiallyFilled&&journal.ReservedBuyAmount==70000,"partial fill preserves the unfilled exposure");
                    Check(!journal.RecordExecution("00000123","F1",3,9900,Utc(10))&&journal.Snapshot.Executions.Count==1,"duplicate execution is idempotent");
                    Reject(()=>journal.RecordExecution("00000123","F1",4,9900,Utc(10)),"duplicate execution with changed facts is rejected");
                    Reject(()=>journal.RecordExecution("00000123","F2",8,9900,Utc(11)),"fill cannot exceed remaining quantity");
                    Reject(()=>journal.RecordExecution("00000123","F2",1,10001,Utc(11)),"buy fill cannot exceed the acknowledged limit price");
                    Check(journal.RecordExecution("00000123","F2",2,9950,Utc(5)),"out-of-order distinct execution can be reconciled by ID");
                    Check(journal.Snapshot.LastBrokerChangeUtc==Utc(10),"out-of-order events cannot move snapshot invalidation time backwards");
                    var copy=journal.Snapshot;copy.Orders[0].Quantity=1;
                    Check(journal.Snapshot.Orders[0].Quantity==10,"external snapshot mutation cannot change the actual ledger");
                    Reject(()=>new LiveOrderJournal(path),"simultaneous writers cannot race an actual-order journal");
                }
                var xml=File.ReadAllText(path);
                Check(!xml.Contains(credentials.Account)&&!xml.Contains(credentials.Password)&&xml.Contains("RealAccount"),"durable order journal contains neither account digits nor password");
                using(var recovered=new LiveOrderJournal(path)) {
                    Check(recovered.RecoveryRequired&&recovered.Snapshot.Orders[0].Status==LiveOrderStatus.Unknown&&recovered.ReservedBuyAmount==50000,"restart latches unresolved real order unknown without releasing exposure");
                    Reject(()=>recovered.Prepare(Buy("new-after-crash","000660"),Utc(20)),"unknown broker state blocks new orders for any stock");
                    Reject(()=>recovered.ReconcileWorking(firstId,"00000123",10,7,3,Utc(20)),"aggregate filled amount cannot invent executions missing from the journal");
                    Reject(()=>recovered.ImportRecoveryExecution(firstId,"00000999","F3",2,9900,Utc(20)),"recovery execution must match the known broker order");
                    Check(recovered.ImportRecoveryExecution(firstId,"00000123","F3",2,9900,Utc(20)),"missing authoritative execution can be ingested under unknown status");
                    Check(recovered.RecoveryRequired&&recovered.Snapshot.Orders[0].Status==LiveOrderStatus.Unknown,"importing executions alone does not clear reconciliation");
                    recovered.ReconcileWorking(firstId,"00000123",10,7,3,Utc(21),"20261112");
                    Check(!recovered.RecoveryRequired&&recovered.Snapshot.Orders[0].FilledQuantity==7&&recovered.Snapshot.Orders[0].Status==LiveOrderStatus.PartiallyFilled,"matching authoritative executions and open quantity recover a restarted order");
                    var cancellation=Accepted(recovered,Modify(LiveOrderKind.Cancel,"cancel-a","00000123",3),"00000124",Utc(22),8);
                    Check(cancellation.Status==LiveOrderStatus.AwaitingConfirmation&&recovered.ReservedBuyAmount==30000,"cancel acknowledgement does not release original outstanding exposure");
                    recovered.ConfirmCancellation("00000123","00000124","C1",3,Utc(23));
                    Check(recovered.Snapshot.Orders.Single(o=>o.Id==firstId).Status==LiveOrderStatus.Cancelled&&recovered.ReservedBuyAmount==0,"authoritative cancellation confirmation releases only confirmed remainder");
                    recovered.ConfirmCancellation("00000123","00000124","C1",3,Utc(23));
                    Check(recovered.Snapshot.Orders.Single(o=>o.Id==firstId).CancelledQuantity==3,"duplicate cancel confirmation cannot release twice");
                    Reject(()=>recovered.ConfirmCancellation("00000123","00000124","C1",2,Utc(23)),"contradictory duplicate cancellation evidence is rejected");
                }

                using(var journal=new LiveOrderJournal(Path.Combine(directory,"ack-errors.xml"))) {
                    var order=journal.Prepare(Buy("zero"),Utc());journal.MarkDispatching(order.Id,Utc());journal.MarkRequestAccepted(order.Id,7,Utc());journal.RecordAcknowledgement(order.Id,7,"0000",false,Utc());
                    Check(journal.Snapshot.Orders[0].Status==LiveOrderStatus.Rejected&&journal.Snapshot.Executions.Count==0,"official order number zero means rejection even ErrorState is zero");
                    order=journal.Prepare(Buy("blank"),Utc());journal.MarkDispatching(order.Id,Utc());journal.MarkRequestAccepted(order.Id,8,Utc());journal.RecordAcknowledgement(order.Id,8,"",false,Utc());
                    Check(journal.RecoveryRequired&&journal.Snapshot.Orders[1].Status==LiveOrderStatus.Unknown,"blank/unreadable order acknowledgement is unknown rather than fabricated rejection");
                    journal.BindAccount(credentials.Account);
                    var evidence=new LiveRecoveryOrderEvidence{AccountFingerprint=journal.Snapshot.AccountFingerprint,BrokerOrderNumber="0000901",TradingDate="20261112",Code="005930",Kind=LiveOrderKind.Buy,TradeKind=LiveOrderKind.Buy,Quantity=10,LimitPrice=10000,ReceivedUtc=Utc(10),AccountMatched=true,SnapshotComplete=true};
                    Reject(()=>journal.BindRecoveryOrder(order.Id,evidence,Utc(10)),"similar order fields do not establish unknown submission identity");
                    evidence.IdentityProven=true;evidence.Code="000660";
                    Reject(()=>journal.BindRecoveryOrder(order.Id,evidence,Utc(10)),"recovery binding rejects mismatched broker stock identity");
                    evidence.Code="005930";journal.BindRecoveryOrder(order.Id,evidence,Utc(10));
                    Check(journal.RecoveryRequired&&journal.Snapshot.Orders[1].BrokerOrderNumber=="0000901","verified broker identity attaches to an unacknowledged unknown submission without clearing recovery");
                    Check(journal.ImportRecoveryExecution(order.Id,"0000901","RECOVERY-F1",4,9900,Utc(11)),"unknown before acknowledgement can ingest authoritative executions after identity binding");
                    journal.ReconcileWorking(order.Id,"0000901",10,4,6,Utc(12),"20261112");
                    Check(!journal.RecoveryRequired&&journal.Snapshot.Orders[1].Status==LiveOrderStatus.PartiallyFilled,"identity binding plus real execution IDs and exact open snapshot recover the no-ack timeout case");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"amend.xml"))) {
                    var original=Accepted(journal,Buy(),"00011",Utc());
                    Reject(()=>journal.Prepare(Modify(LiveOrderKind.Amend,"partial-amend","00011",5,11000),Utc(1)),"unsupported partial amendment does not silently rewrite the original order");
                    var amendment=Accepted(journal,Modify(LiveOrderKind.Amend,"amend-full","00011",10,11000),"00012",Utc(1),8);
                    Check(journal.ReservedBuyAmount==110000,"pending higher-price amendment reserves incremental exposure without double counting");
                    Reject(()=>journal.RecordExecution("00012","premature",1,11000,Utc(2)),"amend acknowledgement is not replacement confirmation");
                    journal.ConfirmAmendment("00011","00012","A1",10,11000,Utc(3));
                    Check(journal.Snapshot.Orders.Single(o=>o.Id==original.Id).Status==LiveOrderStatus.Replaced&&journal.Snapshot.Orders.Single(o=>o.Id==amendment.Id).Status==LiveOrderStatus.Working,"authoritative amendment confirmation activates a replacement chain");
                    Check(journal.RecordExecution("00012","new-fill",10,10900,Utc(4))&&journal.Snapshot.Orders.Single(o=>o.Id==amendment.Id).FilledQuantity==10,"confirmed replacement accepts executions using its inherited buy side");
                    Check(journal.ReservedBuyAmount==0,"completed replacement has no outstanding buy exposure");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"day-identity.xml"))) {
                    Accepted(journal,Buy("day1"),"001",Utc());journal.RecordExecution("001","F1",10,10000,Utc(1));
                    var next=Utc().AddDays(1);Accepted(journal,Buy("day2"),"001",next,7);journal.RecordExecution("001","F1",10,10000,next.AddSeconds(1));
                    Check(journal.Snapshot.Orders.Count==2&&journal.Snapshot.Executions.Count==2,"daily broker order and execution identifiers can be reused without cross-day attachment");
                    Check(journal.Snapshot.Executions.Select(f=>f.TradingDate).Distinct().Count()==2,"execution identity carries the originating trading date");
                }
                string corrupt=Path.Combine(directory,"corrupt.xml");
                using(var journal=new LiveOrderJournal(corrupt)){Accepted(journal,Buy(),"1",Utc());}
                var document=new XmlDocument();document.Load(corrupt);document.SelectSingleNode("/LiveOrderJournalState/Orders/LiveOrderRecord/FilledQuantity").InnerText="9";document.Save(corrupt);
                Reject(()=>new LiveOrderJournal(corrupt),"corrupt execution totals stop loading instead of resetting actual-order history");

                string faulty=Path.Combine(directory,"storage.xml");
                using(var journal=new LiveOrderJournal(faulty)) {
                    File.Move(faulty,faulty+".preserved");Directory.CreateDirectory(faulty);
                    Reject(()=>journal.Prepare(Buy(),Utc()),"durable save failure is reported before submission");
                    Check(journal.StorageFault,"actual-order journal latches a storage fault");
                    Reject(()=>journal.Prepare(Buy("retry"),Utc()),"storage failure cannot be bypassed with another order key");
                }
                return checks;
            }finally{Directory.Delete(directory,true);}
        }
    }
}
