using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace ShinhanNativeTrader {
    public static class LiveOrdersTests {
        sealed class FakeWire : ILiveOrderWire,ILiveOrderChannelPreparation {
            public event Action<LiveOrderAcknowledgement> Acknowledged;
            public int Sends,Preparations;public bool Disposed;
            public void PrepareChannel(){Preparations++;}
            public int ReturnId=7;public bool ThrowOnSend;
            public Action BeforeSend,DuringSend;
            public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc){
                bool entered=false;
                try {
                    LiveOrderSafety.EnsureAllowed(checks,credentials,utc);
                    LiveOrderSafety.ValidatePreflight(intent,checks,utc,DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc));
                    if(BeforeSend!=null)BeforeSend();
                    LiveOrderSafety.EnsureAllowed(checks,credentials,utc);
                    LiveOrderSafety.ValidatePreflight(intent,checks,utc,DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc));
                    entered=true;Sends++;if(DuringSend!=null)DuringSend();if(ThrowOnSend)throw new InvalidOperationException("account-password-SECRET");return ReturnId;
                }catch{if(!entered)throw new LiveOrderNotSentException();throw new InvalidOperationException("unknown transport response");}
            }
            public void Emit(LiveOrderAcknowledgement value){if(Acknowledged!=null)Acknowledged(value);}
            public void Dispose(){Disposed=true;}
        }
        static int checks;
        static void Check(bool value,string label){checks++;if(!value)throw new Exception("LiveOrdersTests: "+label);}
        static void Reject(Action action,string label){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,label);}
        static DateTime Utc(int seconds=0){return new DateTime(2026,11,12,1,0,0,DateTimeKind.Utc).AddSeconds(seconds);}
        static LiveOrderIntent Buy(string key="buy-a",string code="005930",int quantity=10,decimal price=10000){return new LiveOrderIntent{Kind=LiveOrderKind.Buy,ClientKey=key,Code=code,Quantity=quantity,LimitPrice=price};}
        static LiveOrderIntent Modify(LiveOrderKind kind,string key,string original,int qty=10,decimal price=10000){return new LiveOrderIntent{Kind=kind,ClientKey=key,Code="005930",OriginalBrokerOrderNumber=original,Quantity=qty,LimitPrice=price};}
        static LiveOrderPreflight Ready(DateTime utc){return new LiveOrderPreflight{EvidenceStillCurrent=()=>true,AccountAuthorized=true,OrderableAmountVerified=true,SellableQuantityVerified=true,OrdersReconciled=true,FillsReconciled=true,RecoveryVerified=true,PriceRuleVerified=true,QuoteVerified=true,Cash100Orderable=1000000,CapacityAmount=1000000,OperatingLimit=1000000,CapacityQuantity=100,SellableQuantity=100,SnapshotUtc=utc,QuoteUtc=utc};}
        static LiveOrderPreflight Authorized(DateTime utc,LiveAccountCredentials credentials) {
            var ready=Ready(utc);ready.Authorization=new LiveTradingSession();ready.Authorization.Authorize(credentials.Account,utc);return ready;
        }
        static LiveOrderRecord Accepted(LiveOrderJournal journal,LiveOrderIntent intent,string number,DateTime utc,int rid=7) {
            var order=journal.Prepare(intent,utc);journal.MarkDispatching(order.Id,utc);journal.MarkRequestAccepted(order.Id,rid,utc);journal.RecordAcknowledgement(order.Id,rid,number,false,utc);return journal.Snapshot.Orders.Single(o=>o.Id==order.Id);
        }
        static LiveOrderRecord AcceptedWithEvidence(LiveOrderJournal journal,LiveOrderIntent intent,string number,DateTime utc,int rid=7) {
            var order=journal.Prepare(intent,utc);journal.MarkDispatching(order.Id,utc);journal.MarkRequestAccepted(order.Id,rid,utc);
            var slots=new[]{"0",number,"0","","0","synthetic accepted","0","0"};
            var response=LiveOrderResponseParser.Read(rid,index=>slots[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
            journal.RecordAcknowledgement(order.Id,rid,response.BrokerOrderNumber,false,utc,response);
            return journal.Snapshot.Orders.Single(value=>value.Id==order.Id);
        }
        static BrokerTerminalFillEvidence TerminalEvidence(LiveOrderJournal journal,LiveOrderRecord order) {
            return new BrokerTerminalFillEvidence{AccountFingerprint=journal.Snapshot.AccountFingerprint,TradingDate=order.TradingDate,
                QueryName="SABA231Q1",RequestScopeVerified=true,MatchingOrderCount=1,RequestStartedUtc=Utc(3),ReceivedUtc=Utc(4),
                Row=new BrokerOrderRow{OrderNumber=order.BrokerOrderNumber.TrimStart('0'),OriginalOrderNumber="0",Code=order.Code,
                    SideCode=order.TradeKind==LiveOrderKind.Buy?"2":"1",AmendCancelCode="0",ProductCode="01",CreditCode="00",ConfirmationCode="0",
                    Quantity=order.Quantity,LimitPrice=order.LimitPrice,FilledQuantity=order.Quantity,OpenQuantity=0,ConfirmedQuantity=0,
                    AutoCancelledQuantity=0,AverageFillPrice=order.TradeKind==LiveOrderKind.Buy?9900:10100}};
        }
        static void TerminalRecoveryTests(string directory) {
            string path=Path.Combine(directory,"terminal-checkpoint.xml");string id;
            using(var journal=new LiveOrderJournal(path)) {
                journal.BindAccount("12345678901");var order=AcceptedWithEvidence(journal,Buy("terminal-known"),"00000812345",Utc());id=order.Id;
                journal.MarkUnknown(order.Id,Utc(2));
                var mutations=new Action<BrokerTerminalFillEvidence>[] {
                    value=>value.RequestScopeVerified=false,value=>value.MatchingOrderCount=0,value=>value.MatchingOrderCount=2,
                    value=>value.QueryName="TR_5200",value=>value.AccountFingerprint="wrong-account",value=>value.TradingDate="20261111",
                    value=>value.Row.OrderNumber="888888",value=>value.Row.Code="000660",value=>value.Row.SideCode="1",
                    value=>value.Row.ProductCode="02",value=>value.Row.CreditCode="01",value=>value.Row.ConfirmationCode="1",
                    value=>value.Row.Quantity=11,value=>value.Row.LimitPrice=10001,value=>value.Row.FilledQuantity=9,
                    value=>value.Row.OpenQuantity=1,value=>value.Row.ConfirmedQuantity=1,value=>value.Row.AutoCancelledQuantity=1,
                    value=>value.Row.AverageFillPrice=0,value=>value.Row.AverageFillPrice=10001,value=>value.Row.OriginalOrderNumber="123",
                    value=>value.RequestStartedUtc=Utc(2),value=>value.RequestStartedUtc=Utc(5),value=>value.ReceivedUtc=Utc(6)
                };
                string[] labels={"unverified request","missing row","ambiguous row","wrong query","wrong account","wrong day","wrong broker ID",
                    "wrong stock","wrong direction","wrong product","credit trade","broker rejection","wrong quantity","wrong limit",
                    "partial fill","open remainder","confirmation quantity","automatic cancellation","missing average","limit-violating average",
                    "original order link","pre-Unknown request","request after receive","future receive"};
                for(int index=0;index<mutations.Length;index++) {
                    var evidence=TerminalEvidence(journal,order);mutations[index](evidence);
                    Reject(()=>journal.ReconcileKnownTerminalFill(id,evidence,Utc(5)),"terminal checkpoint rejects "+labels[index]);
                }
                Reject(()=>journal.ReconcileKnownTerminalFill(id,TerminalEvidence(journal,order),Utc(10)),"stale terminal broker snapshot cannot recover an unknown order");
                Check(journal.RecoveryRequired&&journal.Snapshot.Executions.Count==0&&journal.Snapshot.Orders.Single().FillCheckpoint==null,
                    "rejected terminal evidence leaves the original unknown record and individual execution ledger untouched");
                Check(journal.ReconcileKnownTerminalFill(id,TerminalEvidence(journal,order),Utc(5)),"fresh exact account-scoped terminal snapshot recovers the already accepted order");
                var recovered=journal.Snapshot.Orders.Single();
                Check(recovered.Status==LiveOrderStatus.Filled&&recovered.FilledQuantity==10&&recovered.FillCheckpoint.FilledQuantity==10
                    &&recovered.FillCheckpoint.AverageFillPrice==9900&&journal.Snapshot.Executions.Count==0&&!journal.RecoveryRequired&&journal.ReservedBuyAmount==0,
                    "terminal aggregate is persisted separately without inventing individual executions or retaining a filled buy reserve");
                Check(LiveOrderJournal.HasTerminalFillCheckpoint(recovered,journal.Snapshot.AccountFingerprint)
                    &&!LiveOrderJournal.HasTerminalFillCheckpoint(recovered,"other-account"),"terminal checkpoint helper validates the stored account identity");
                var detached=recovered.FillCheckpoint;detached.AverageFillPrice=1;
                Check(journal.Snapshot.Orders.Single().FillCheckpoint.AverageFillPrice==9900,"caller mutation cannot replace the durable broker aggregate checkpoint");
                Reject(()=>journal.RecordExecution("812345","late-new-id",1,9900,Utc(6)),"terminal checkpoint cannot accept an extra execution beyond the reported full fill");
            }
            using(var restarted=new LiveOrderJournal(path)) {
                var recovered=restarted.Snapshot.Orders.Single();
                Check(!restarted.RecoveryRequired&&recovered.Status==LiveOrderStatus.Filled&&recovered.FilledQuantity==10
                    &&restarted.Snapshot.Executions.Count==0&&recovered.FillCheckpoint.QueryName=="SABA231Q1",
                    "restart preserves terminal snapshot recovery without manufacturing an execution ID or reopening the filled order");
                restarted.MarkBrokerEvidenceUnknown(id,Utc(6));
                Check(restarted.RecoveryRequired&&!LiveOrderJournal.HasTerminalFillCheckpoint(restarted.Snapshot.Orders.Single(),restarted.Snapshot.AccountFingerprint),
                    "later contradictory broker evidence can invalidate a terminal checkpoint without erasing its previous observation");
                var evidence=TerminalEvidence(restarted,recovered);evidence.RequestStartedUtc=Utc(7);evidence.ReceivedUtc=Utc(8);
                Check(restarted.ReconcileKnownTerminalFill(id,evidence,Utc(8)),"only a newer full account-scoped snapshot can re-establish a contradicted terminal checkpoint");
            }
            var document=new XmlDocument();document.Load(path);
            document.SelectSingleNode("/LiveOrderJournalState/Orders/LiveOrderRecord/FillCheckpoint/AccountFingerprint").InnerText="tampered-account";
            document.Save(path);Reject(()=>new LiveOrderJournal(path),"tampered persisted terminal-checkpoint identity fails closed on restart");

            foreach(var kind in new[]{LiveOrderKind.Buy,LiveOrderKind.Sell}) {
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-partial-"+kind+".xml"))) {
                    journal.BindAccount("12345678901");var intent=Buy("partial-known-"+kind);intent.Kind=kind;
                    var order=AcceptedWithEvidence(journal,intent,"000812346",Utc());
                    decimal validPrice=kind==LiveOrderKind.Buy?9900:10100;
                    journal.RecordExecution("812346","real-AA-subset",3,validPrice,Utc(1));journal.MarkUnknown(order.Id,Utc(2));
                    var evidence=TerminalEvidence(journal,order);
                    Check(journal.ReconcileKnownTerminalFill(order.Id,evidence,Utc(5)),kind+" terminal snapshot may cover a genuine earlier partial execution subset");
                    Check(journal.Snapshot.Executions.Count==1&&journal.Snapshot.Executions.Single().Quantity==3
                        &&journal.Snapshot.Orders.Single().FilledQuantity==10,
                        kind+" checkpoint total is not added on top of the real AA subset");
                    Check(!journal.RecordExecution("812346","real-AA-subset",3,validPrice,Utc(6)),kind+" real prior execution ID remains idempotent after terminal recovery");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-inconsistent-value-"+kind+".xml"))) {
                    journal.BindAccount("12345678901");var intent=Buy("bad-partial-"+kind);intent.Kind=kind;
                    var order=AcceptedWithEvidence(journal,intent,"000812347",Utc());
                    journal.RecordExecution("812347","real-AA-inconsistent",3,kind==LiveOrderKind.Buy?9000:11000,Utc(1));journal.MarkUnknown(order.Id,Utc(2));
                    Reject(()=>journal.ReconcileKnownTerminalFill(order.Id,TerminalEvidence(journal,order),Utc(5)),
                        kind+" aggregate must explain a limit-valid remaining notional after its real AA subset");
                    Check(journal.RecoveryRequired&&journal.Snapshot.Orders.Single().FilledQuantity==3&&journal.Snapshot.Orders.Single().FillCheckpoint==null,
                        kind+" inconsistent aggregate cannot erase or supplement existing individual fills");
                }
                string invalidPricePath=Path.Combine(directory,"terminal-invalid-known-price-"+kind+".xml");string invalidPriceId;
                using(var journal=new LiveOrderJournal(invalidPricePath)) {
                    journal.BindAccount("12345678901");var intent=Buy("invalid-price-"+kind);intent.Kind=kind;
                    var order=AcceptedWithEvidence(journal,intent,"000812350",Utc());invalidPriceId=order.Id;
                    journal.RecordExecution("812350","real-AA-price",1,kind==LiveOrderKind.Buy?9900:10100,Utc(1));journal.MarkUnknown(order.Id,Utc(2));
                }
                var invalidPriceXml=new XmlDocument();invalidPriceXml.Load(invalidPricePath);
                invalidPriceXml.SelectSingleNode("/LiveOrderJournalState/Executions/LiveExecutionRecord/Price").InnerText=kind==LiveOrderKind.Buy?"11000":"9000";
                invalidPriceXml.Save(invalidPricePath);
                using(var journal=new LiveOrderJournal(invalidPricePath)) {
                    var order=journal.Snapshot.Orders.Single();
                    Reject(()=>journal.ReconcileKnownTerminalFill(invalidPriceId,TerminalEvidence(journal,order),Utc(5)),
                        kind+" terminal recovery rejects an individual price outside the original limit even when aggregate and remaining value could fit");
                }
            }
            using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-no-accepted-proof.xml"))) {
                journal.BindAccount("12345678901");var order=Accepted(journal,Buy("legacy-without-proof"),"812348",Utc());journal.MarkUnknown(order.Id,Utc(2));
                Reject(()=>journal.ReconcileKnownTerminalFill(order.Id,TerminalEvidence(journal,order),Utc(5)),"positive ID without preserved accepted response is not a terminal-recovery identity proof");
            }
            using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-no-id.xml"))) {
                journal.BindAccount("12345678901");var order=journal.Prepare(Buy("no-id"),Utc());journal.MarkUnknown(order.Id,Utc(2));
                var evidence=TerminalEvidence(journal,new LiveOrderRecord{BrokerOrderNumber="812349",Code="005930",TradingDate="20261112",Quantity=10,LimitPrice=10000,TradeKind=LiveOrderKind.Buy});
                Reject(()=>journal.ReconcileKnownTerminalFill(order.Id,evidence,Utc(5)),"matching stock and amount never attach a broker order to an unacknowledged unknown request");
            }
            using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-wrong-venue.xml"))) {
                journal.BindAccount("12345678901");var order=journal.Prepare(Buy("wrong-venue"),Utc());
                journal.MarkDispatching(order.Id,Utc());journal.MarkRequestAccepted(order.Id,7,Utc());
                var values=new[]{"812351","0","0","","0","synthetic SOR","0","0"};
                var response=LiveOrderResponseParser.Read(7,index=>values[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
                journal.RecordAcknowledgement(order.Id,7,response.BrokerOrderNumber,false,Utc(),response);journal.MarkUnknown(order.Id,Utc(2));
                var evidence=TerminalEvidence(journal,journal.Snapshot.Orders.Single());
                Reject(()=>journal.ReconcileKnownTerminalFill(order.Id,evidence,Utc(5)),"modern SOR identity cannot be recovered as the requested KRX order by a numeric match alone");
            }
            foreach(var kind in new[]{LiveOrderKind.Amend,LiveOrderKind.Cancel}) {
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-child-"+kind+".xml"))) {
                    journal.BindAccount("12345678901");AcceptedWithEvidence(journal,Buy("child-parent"),"812352",Utc());
                    var child=AcceptedWithEvidence(journal,Modify(kind,"child-"+kind,"812352"),"812353",Utc(1),8);journal.MarkUnknown(child.Id,Utc(2));
                    Reject(()=>journal.ReconcileKnownTerminalFill(child.Id,TerminalEvidence(journal,child),Utc(5)),
                        kind+" command cannot be recovered as a plain new order from a terminal aggregate row");
                }
            }
            using(var journal=new LiveOrderJournal(Path.Combine(directory,"terminal-global-barrier.xml"))) {
                journal.BindAccount("12345678901");var order=AcceptedWithEvidence(journal,Buy("barrier-target"),"812354",Utc());
                var other=AcceptedWithEvidence(journal,Buy("barrier-other","000660"),"812355",Utc(1),8);
                journal.MarkUnknown(order.Id,Utc(2));journal.MarkBrokerEvidenceUnknown(other.Id,Utc(5));
                var evidence=TerminalEvidence(journal,order);evidence.ReceivedUtc=Utc(6);
                Reject(()=>journal.ReconcileKnownTerminalFill(order.Id,evidence,Utc(6)),"a request preceding another broker change cannot provide a fresh terminal checkpoint");
            }
        }
        public static int Run() {
            checks=0;
            int startupTraces=0;
            using(var lazy=new IndiLiveOrderConnection(null,message=>startupTraces++))
                Check(startupTraces==0,"creating an account's order connection performs no native initialization or schema inspection");
            var directory=Path.Combine(Path.GetTempPath(),"ShinhanLiveOrderTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try {
                using(var preparationJournal=new LiveOrderJournal(Path.Combine(directory,"prepare-only.xml")))using(var preparationWire=new FakeWire()) {
                    var preparationGateway=new LiveOrderGateway(preparationJournal,preparationWire);
                    Check(preparationWire.Preparations==0&&preparationWire.Sends==0,"gateway construction leaves initial read-only account queries independent of order preparation");
                    preparationGateway.PrepareChannel();
                    Check(preparationWire.Preparations==1&&preparationWire.Sends==0&&preparationJournal.Snapshot.Orders.Count==0,"explicit channel preparation sends no order and creates no journal request");
                    preparationGateway.Dispose();Reject(()=>preparationGateway.PrepareChannel(),"disposed channel cannot initialize deferred native state");
                }
                var credentials=new LiveAccountCredentials("12345678901","Z!pw");
                var intent=Buy();var fields=LiveOrderProtocol.BuildFields(intent,credentials);
                Check(LiveOrderProtocol.QueryName(intent)=="SABA101U1","cash buy selects official new-order query");
                Check(fields[0]==credentials.Account&&fields[1]=="01"&&fields[2]==credentials.Password,"credentials are forwarded only in transient request fields");
                Check(fields[5]=="0"&&fields[6]=="00"&&fields[7]=="2"&&fields[8]=="A005930","buy is regular cash stock without credit/shorting");
                Check(fields[9]=="10"&&fields[10]=="10000"&&fields[11]=="1"&&fields[12]=="2","explicit quantity and regular-session limit price are used");
                Check(fields[16]==""&&fields[21]=="Y"&&!fields.ContainsKey(22),"new order requests result messages without bypassing validation flags");
                var installedNew=new IndiOrderSchemaInfo{Kind=IndiOrderSchemaKind.Modern,QueryName="SABA101U1",InputCount=39,OutputCount=8,
                    SerializationVersion=0x0208,KrxRouteInputIndex=37,KrxRouteCode="1",RouteSource="PeerMatched"};
                foreach(var side in new[]{LiveOrderKind.Buy,LiveOrderKind.Sell}) {
                    var routed=Buy("actual-route-"+side);routed.Kind=side;
                    var routedFields=LiveOrderProtocol.BuildFields(routed,credentials);LiveOrderProtocol.ApplyInstalledSchema(routed,routedFields,installedNew);
                    Check(routedFields[37]=="1"&&routedFields[7]==(side==LiveOrderKind.Buy?"2":"1"),"both new-order sides use the actual shared-domain KRX1 mapping rather than SOR0");
                    Check(routedFields[36]==""&&routedFields[38]==""&&routedFields[21]=="Y"&&!routedFields.ContainsKey(22),"ordinary KRX limit order clears conditional/SOR fields without disabling broker checks");
                }
                var installedAmend=new IndiOrderSchemaInfo{Kind=IndiOrderSchemaKind.Modern,QueryName="SABA102U1",InputCount=38,OutputCount=8,
                    SerializationVersion=0x0208,KrxRouteInputIndex=35,KrxRouteCode="1",RouteSource="OwnHelper"};
                foreach(var kind in new[]{LiveOrderKind.Amend,LiveOrderKind.Cancel}) {
                    var routed=Modify(kind,"actual-route-"+kind,"00000123");var routedFields=LiveOrderProtocol.BuildFields(routed,credentials);
                    LiveOrderProtocol.ApplyInstalledSchema(routed,routedFields,installedAmend);
                    Check(routedFields[35]=="1"&&routedFields[16]=="00000123"&&routedFields[36]==""&&routedFields[37]=="","actual 38-input KRX amend/cancel keeps its original exchange number and clears SOR identity");
                }
                Reject(()=>LiveOrderProtocol.ApplyInstalledSchema(Buy(),LiveOrderProtocol.BuildFields(Buy(),credentials),installedAmend),"new order cannot bind a different query's field positions");
                installedNew.KrxRouteCode=null;
                Reject(()=>LiveOrderProtocol.ApplyInstalledSchema(Buy(),LiveOrderProtocol.BuildFields(Buy(),credentials),installedNew),"unverified route still cannot become an emitted order field");
                installedNew.KrxRouteCode="1";installedNew.KrxRouteInputIndex=35;
                Reject(()=>LiveOrderProtocol.ApplyInstalledSchema(Buy(),LiveOrderProtocol.BuildFields(Buy(),credentials),installedNew),"a wrong new-order routing offset is rejected before field emission");
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
                Check(LiveOrderProtocol.BrokerNumberKey("00000123")=="123"&&LiveOrderProtocol.BrokerNumberKey("0000")==null&&LiveOrderProtocol.BrokerNumbersEqual("123","000123"),"numeric broker identity ignores padding while invalid zero identifiers remain invalid");

                intent=Buy();var ready=Ready(Utc());
                LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1));Check(true,"fresh authoritative preflight values validate independently of account authorization");
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
                ready=Ready(Utc());ready.EvidenceStillCurrent=null;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"orders require a live evidence epoch callback rather than detached booleans");
                ready=Ready(Utc());ready.EvidenceStillCurrent=()=>false;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"an invalidated account-evidence epoch blocks dispatch");
                ready=Ready(Utc());ready.EvidenceStillCurrent=()=>{throw new InvalidOperationException("PRIVATE-DATA");};
                string callbackError="";try{LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1));}catch(Exception error){callbackError=error.Message;}
                Check(callbackError!=""&&!callbackError.Contains("PRIVATE-DATA"),"failed evidence callbacks fail closed without leaking provider/runtime text");
                intent.Kind=LiveOrderKind.Sell;ready=Ready(Utc());ready.SellableQuantity=9;Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"sellable stock quantity is checked separately from holdings");
                intent=Modify(LiveOrderKind.Amend,"amend-check","00000123");ready=Ready(Utc());
                Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"amendment cannot bypass the original-side verification");
                ready.OriginalOrderVerified=true;ready.OriginalTradeKind=LiveOrderKind.Buy;ready.Cash100Orderable=1;
                Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"buy-side amendment still checks fresh orderable cash");
                ready=Ready(Utc());ready.OriginalOrderVerified=true;ready.OriginalTradeKind=LiveOrderKind.Sell;ready.SellableQuantity=1;
                Reject(()=>LiveOrderSafety.ValidatePreflight(intent,ready,Utc(),Utc(-1)),"sell-side amendment still checks sellable quantity");
                var authorization=new LiveTradingSession();
                Check(!authorization.Active&&!authorization.MatchesAccount(credentials.Account),"each process starts without account trading authorization");
                Reject(()=>LiveOrderSafety.EnsureAllowed(Ready(Utc()),credentials,Utc()),"missing explicit account authorization blocks order dispatch");
                authorization.Authorize(credentials.Account,Utc());
                Check(authorization.Active&&authorization.MatchesAccount(credentials.Account)&&!authorization.MatchesAccount("10987654321"),"explicit authorization is bound to one account");
                ready=Ready(Utc());ready.Authorization=authorization;LiveOrderSafety.EnsureAllowed(ready,credentials,Utc());Check(true,"authorized account can pass the authorization gate without falsely changing broker evidence");
                ready.OrdersReconciled=false;Reject(()=>LiveOrderSafety.ValidatePreflight(Buy(),ready,Utc(),Utc(-1)),"authorization cannot replace missing broker reconciliation");
                authorization.Revoke();Check(!authorization.Active&&!authorization.MatchesAccount(credentials.Account),"stop revokes memory-only authorization");
                Reject(()=>LiveOrderSafety.EnsureAllowed(ready,credentials,Utc()),"revoked authorization cannot be reused by cached preflight");
                authorization.Authorize(credentials.Account,Utc(10));
                Reject(()=>LiveOrderSafety.EnsureAllowed(ready,credentials,Utc()),"future-dated approval does not authorize an earlier dispatch");
                Reject(()=>authorization.Authorize(credentials.Account,DateTime.SpecifyKind(Utc(),DateTimeKind.Unspecified)),"authorization requires an explicit UTC timestamp");
                authorization.Authorize(credentials.Account,Utc());ready=Ready(Utc());ready.Authorization=authorization;
                Reject(()=>LiveOrderSafety.EnsureAllowed(ready,credentials,Utc().AddDays(1)),"missed UI close or overnight suspend cannot carry authorization into a second trading day");
                var evening=new DateTime(2026,11,12,11,0,0,DateTimeKind.Utc);authorization.Authorize(credentials.Account,evening);
                Check(authorization.TradingDateKorea==new DateTime(2026,11,13)&&authorization.ValidUntilUtc==new DateTime(2026,11,13,6,20,0,DateTimeKind.Utc),"evening approval targets the next weekday session for unattended operation");
                Reject(()=>LiveOrderSafety.EnsureAllowed(ready,credentials,evening),"next-day approval cannot submit on the preceding evening");
                LiveOrderSafety.EnsureAllowed(ready,credentials,new DateTime(2026,11,13,0,0,0,DateTimeKind.Utc));Check(true,"an explicit evening approval is valid during its intended next session");
                Reject(()=>LiveOrderSafety.EnsureAllowed(ready,credentials,new DateTime(2026,11,13,6,20,0,DateTimeKind.Utc)),"authorization expires at the exact session cutoff independently of UI timers");
                authorization.Authorize(credentials.Account,new DateTime(2026,11,13,11,0,0,DateTimeKind.Utc));Check(authorization.TradingDateKorea==new DateTime(2026,11,16),"Friday evening approval skips weekend sessions without pretending to know exchange holidays");
                Reject(()=>new ComLiveOrderWire(new object()),"COM order wire requires an actual hosted native control");

                string path=Path.Combine(directory,"actual-orders.xml");string firstId;
                using(var journal=new LiveOrderJournal(path)) {
                    journal.BindAccount(credentials.Account);
                    Reject(()=>journal.BindAccount("10987654321"),"journal cannot be silently reused for another actual account");
                    using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire)) {
                        Reject(()=>gateway.Submit(Buy(),credentials,Ready(Utc()),Utc()),"actual-order gateway requires explicit account authorization before preparing/sending");
                        Check(wire.Sends==0&&journal.Snapshot.Orders.Count==0,"missing authorization cannot create an order or contact a transport");
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
                    journal.MarkBrokerEvidenceUnknown(journal.Snapshot.Orders[1].Id,next.AddSeconds(2));
                    Check(journal.RecoveryRequired&&journal.Snapshot.Orders[1].Status==LiveOrderStatus.Unknown,"contradictory broker evidence can reopen a terminal order as unknown for recovery");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"canonical-identity.xml"))) {
                    var original=Accepted(journal,Buy(),"0000123",Utc());
                    Check(journal.RecordExecution("123","00001",1,9900,Utc(1))&&journal.Snapshot.Executions[0].BrokerOrderNumber=="0000123","execution lookup accepts broker padding differences while preserving captured raw order and execution IDs");
                    var duplicate=journal.Prepare(Buy("alias-other-stock","000660"),Utc(2));journal.MarkDispatching(duplicate.Id,Utc(2));journal.MarkRequestAccepted(duplicate.Id,8,Utc(2));
                    Reject(()=>journal.RecordAcknowledgement(duplicate.Id,8,"123",false,Utc(2)),"same-day canonical broker duplicate cannot attach another stock's order");
                    journal.MarkUnknown(duplicate.Id,Utc(3));
                    Check(journal.Snapshot.Orders.Single(o=>o.Id==original.Id).BrokerOrderNumber=="0000123","canonical comparison never rewrites the original raw broker identifier");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"authorized-reentrant.xml")))using(var wire=new FakeWire()) {
                    var now=Utc();var preflight=Authorized(now,credentials);int notifications=0;
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>now)) {
                        gateway.OrderChanged+=order=>{notifications++;Check(!gateway.Busy&&order.BrokerOrderNumber=="0000701","ack event publishes known broker identity after clearing in-flight state");journal.RecordExecution(order.BrokerOrderNumber,"BUFFERED-AA-F1",3,9900,now);};
                        wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0000701"});
                        Check(wire.Sends==0&&journal.Snapshot.Orders.Count==0,"authorizing a session alone never submits an order");
                        var sent=gateway.Submit(Buy(),credentials,preflight,now);
                        Check(wire.Sends==1&&notifications==1&&sent.Status==LiveOrderStatus.PartiallyFilled&&sent.FilledQuantity==3,"authorized fake transport exercises reentrant ack and buffered unique execution without generating a synthetic fill");
                        Reject(()=>gateway.Submit(Buy(),credentials,preflight,now),"duplicate authorized client key is blocked before a second wire call");
                        Check(wire.Sends==1,"duplicate request cannot produce a second transport side effect");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"authorized-async.xml")))using(var wire=new FakeWire()) {
                    var now=Utc();var preflight=Authorized(now,credentials);int notifications=0;
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>now)) {
                        gateway.OrderChanged+=order=>notifications++;
                        var sent=gateway.Submit(Buy(),credentials,preflight,now);
                        Check(gateway.Busy&&sent.Status==LiveOrderStatus.AwaitingAcknowledgement&&sent.FilledQuantity==0,"authorized send remains pending without treating request acceptance as fill");
                        wire.Emit(new LiveOrderAcknowledgement{RequestId=99,BrokerOrderNumber="0000801"});
                        Check(gateway.Busy&&notifications==0,"unrelated asynchronous request ID is ignored");
                        now=Utc(1);wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0000801"});
                        Check(!gateway.Busy&&notifications==1&&journal.Snapshot.Orders[0].Status==LiveOrderStatus.Working,"matching asynchronous ack produces exactly one order-change notification");
                        wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0000801"});Check(notifications==1,"duplicate async ack cannot republish a broker order");
                        now=Utc(2);journal.RecordExecution("0000801","ACTUAL-F1",1,9900,now);
                        Reject(()=>gateway.Submit(Buy("stale-second","000660"),credentials,preflight,now),"broker mutation invalidates even a recently fetched snapshot before another send");
                        Check(wire.Sends==1,"stale broker state cannot reach the order wire");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"authorized-timeout.xml")))using(var wire=new FakeWire()) {
                    var now=Utc();var preflight=Authorized(now,credentials);int notifications=0;
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>now)) {
                        gateway.OrderChanged+=order=>notifications++;
                        gateway.Submit(Buy(),credentials,preflight,now);now=Utc(19);gateway.Timeout(now);
                        Check(gateway.Busy&&notifications==0,"ack timeout does not expire before twenty seconds");
                        now=Utc(20);gateway.Timeout(now);
                        Check(!gateway.Busy&&journal.RecoveryRequired&&notifications==1,"expired accepted submission becomes unknown and notifies observers");
                        wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0000901"});
                        Check(journal.Snapshot.Orders[0].Status==LiveOrderStatus.Unknown&&notifications==1,"late ack cannot clear timed-out broker uncertainty");
                        var next=Ready(now);next.Authorization=preflight.Authorization;
                        Reject(()=>gateway.Submit(Buy("new-after-timeout","000660"),credentials,next,now),"a fresh authorized preflight cannot bypass an unresolved submission");
                        Check(wire.Sends==1,"unknown submission is never automatically retried");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"revoked-session.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    var preflight=Authorized(Utc(),credentials);preflight.Authorization.Revoke();
                    Reject(()=>gateway.Submit(Buy(),credentials,preflight,Utc()),"user stop blocks cached account authorization before any journal mutation");
                    preflight.Authorization.Authorize("10987654321",Utc());
                    Reject(()=>gateway.Submit(Buy(),credentials,preflight,Utc()),"re-authorizing a different account does not grant old credentials permission");
                    Check(wire.Sends==0&&journal.Snapshot.Orders.Count==0,"revoked or wrong-account authorization has zero order side effects");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"gateway-reserve.xml")))using(var wire=new FakeWire()) {
                    var now=Utc();var preflight=Authorized(now,credentials);preflight.OperatingLimit=150000;
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>now)) {
                        wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0001001"});
                        gateway.Submit(Buy(),credentials,preflight,now);
                        var second=Ready(now);second.Authorization=preflight.Authorization;second.OperatingLimit=150000;
                        Reject(()=>gateway.Submit(Buy("second-stock","000660"),credentials,second,now),"gateway centrally enforces cross-stock reserve against configured operating funds");
                        Check(wire.Sends==1&&journal.Snapshot.Orders.Count==1,"reserve failure occurs before preparing or sending the second stock");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"wire-failure.xml")))using(var wire=new FakeWire{ThrowOnSend=true}) {
                    var now=Utc();int notifications=0;var preflight=Authorized(now,credentials);
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>now)) {
                        gateway.OrderChanged+=order=>notifications++;
                        string message="";try{gateway.Submit(Buy(),credentials,preflight,now);}catch(Exception error){message=error.Message;}
                        Check(wire.Sends==1&&journal.RecoveryRequired&&notifications==1,"transport failure after durable dispatch becomes unknown with notification");
                        Check(!message.Contains("SECRET")&&!message.Contains("password"),"provider transport exception contents are not exposed to UI/logging");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"unreadable-ack.xml")))using(var wire=new FakeWire()) {
                    var now=Utc();var preflight=Authorized(now,credentials);
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>now)) {
                        wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,ParseFailed=true});
                        var sent=gateway.Submit(Buy(),credentials,preflight,now);
                        Check(sent.Status==LiveOrderStatus.Unknown&&journal.RecoveryRequired,"getter failure in reentrant ack cannot be turned into broker rejection");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"zero-request.xml")))using(var wire=new FakeWire{ReturnId=0})using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    Reject(()=>gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc()),"nonaccepted request return cannot be treated as a working order");
                    Check(wire.Sends==1&&journal.RecoveryRequired,"failed request-return classification retains uncertainty after possible external side effect");
                    Check(journal.Snapshot.Orders[0].Status==LiveOrderStatus.Unknown&&journal.Snapshot.Orders[0].ClientKey=="buy-a"&&string.IsNullOrEmpty(journal.Snapshot.Orders[0].OriginalClientKey),"request return zero never frees the strategy key as proven unsent");
                    Reject(()=>gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc()),"unknown request return cannot be automatically retried with the same strategy key");
                    Check(wire.Sends==1,"unknown request return has at most one external side effect");
                    Reject(()=>journal.RecordNotSent(journal.Snapshot.Orders[0].Id,Utc()),"an unknown post-entry request cannot be manually relabeled as proven not sent");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"typed-error-after-wire-return.xml")))using(var wire=new FakeWire()) {
                    int clockReads=0;
                    using(var gateway=new LiveOrderGateway(journal,wire,()=>{if(clockReads++==0)throw new LiveOrderNotSentException();return Utc();})) {
                        bool typed=false;try{gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());}catch(LiveOrderNotSentException){typed=true;}catch(InvalidOperationException){}
                        Check(!typed&&wire.Sends==1&&journal.RecoveryRequired&&journal.Snapshot.Orders[0].Status==LiveOrderStatus.Unknown,"a typed error after wire return cannot misclassify a possibly submitted request as not sent");
                    }
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"consumer-failure.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    gateway.OrderChanged+=order=>{throw new InvalidOperationException("buffered AA validation failed");};
                    wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0001101"});
                    var sent=gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());
                    Check(sent.Status==LiveOrderStatus.Unknown&&journal.RecoveryRequired,"failed account-event consumer invalidates reconciliation instead of silently continuing");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"revoke-before-wire.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    var preflight=Authorized(Utc(),credentials);wire.BeforeSend=()=>preflight.Authorization.Revoke();
                    bool notSent=false;try{gateway.Submit(Buy(),credentials,preflight,Utc());}catch(LiveOrderNotSentException){notSent=true;}
                    Check(notSent,"wire revocation before entering RequestData has an explicit not-sent result");
                    Check(wire.Sends==0&&!journal.RecoveryRequired&&journal.ReservedBuyAmount==0,"proven pre-entry revocation releases only the local reservation without inventing broker uncertainty");
                    var audit=journal.Snapshot.Orders[0];
                    Check(audit.Status==LiveOrderStatus.NotSent&&audit.OriginalClientKey=="buy-a"&&audit.ClientKey=="not-sent/"+audit.Id&&!audit.RequestId.HasValue&&string.IsNullOrEmpty(audit.BrokerOrderNumber),"not-sent audit preserves original strategy identity without fake broker identifiers");
                    Check(!gateway.Busy&&journal.Snapshot.Executions.Count==0,"known unsent outcome clears in-flight state and never creates an execution");
                    wire.BeforeSend=null;var retry=Authorized(Utc(),credentials);wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0011801"});
                    var accepted=gateway.Submit(Buy(),credentials,retry,Utc());
                    Check(wire.Sends==1&&accepted.Status==LiveOrderStatus.Working&&accepted.ClientKey=="buy-a"&&journal.Snapshot.Orders.Count==2,"freshly authorized retry can reuse only a proven-unsent strategy key while retaining the audit record");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"evidence-changed-during-input.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    bool current=true;var preflight=Authorized(Utc(),credentials);preflight.EvidenceStillCurrent=()=>current;wire.BeforeSend=()=>current=false;
                    bool notSent=false;try{gateway.Submit(Buy(),credentials,preflight,Utc());}catch(LiveOrderNotSentException){notSent=true;}
                    Check(notSent&&wire.Sends==0&&!journal.RecoveryRequired,"account-event proof invalidation before RequestData is proven not sent rather than an ambiguous broker submission");
                    Check(journal.Snapshot.Orders[0].Status==LiveOrderStatus.NotSent&&journal.ReservedBuyAmount==0,"pre-entry data changes retain a terminal no-send audit and release the local reservation");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"broker-zero-no-retry.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0000"});
                    var rejected=gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());
                    Check(rejected.Status==LiveOrderStatus.Rejected&&rejected.ClientKey=="buy-a"&&string.IsNullOrEmpty(rejected.OriginalClientKey),"actual broker order-number-zero rejection is never relabeled as a local not-sent audit");
                    Reject(()=>gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc()),"explicit broker rejection retains the original strategy key to prevent automatic retry");
                    Check(wire.Sends==1,"broker-zero rejection cannot create a second transport call with the same key");
                    Reject(()=>journal.RecordNotSent(rejected.Id,Utc()),"broker rejection with an accepted request cannot be rewritten to free the original key");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"modern-krx-response.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    var slots=new object[]{"0","00008222","0","9000","0","100000","20","0"};
                    var evidence=LiveOrderResponseParser.Read(7,index=>slots[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{credentials.Account,credentials.Password});
                    wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber=evidence.BrokerOrderNumber,ResponseEvidence=evidence});
                    var accepted=gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());
                    Check(accepted.Status==LiveOrderStatus.Working&&accepted.BrokerOrderNumber=="00008222","identified modern response accepts its KRX identity even when the SOR slot is zero");
                    Check(accepted.ResponseEvidence.Layout==OrderResponseLayout.Modern&&accepted.ResponseEvidence.Slot0OrderNumber=="0"&&accepted.ResponseEvidence.Slot1OrderNumber=="00008222","journal retains actual response layout and separate candidate identifiers");
                    Check(accepted.FilledQuantity==0&&journal.Snapshot.Executions.Count==0,"modern acknowledgement still supplies no execution evidence");
                    evidence.Slot1OrderNumber="00009999";evidence.SafeDiagnostic="CHANGED";
                    var stored=journal.Snapshot.Orders.Single();
                    Check(stored.ResponseEvidence.Slot1OrderNumber=="00008222"&&stored.ResponseEvidence.SafeDiagnostic!="CHANGED","caller changes cannot mutate the response copied into the durable journal");
                    stored.ResponseEvidence.Slot1OrderNumber="00001111";
                    Check(journal.Snapshot.Orders.Single().ResponseEvidence.Slot1OrderNumber=="00008222","snapshot callers also receive detached response evidence");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"unknown-layout-response.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    var slots=new object[]{"0","00008333","0","9001","0","","",""};
                    var evidence=LiveOrderResponseParser.Read(7,index=>slots[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Unknown,new[]{credentials.Account,credentials.Password});
                    wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0",ResponseEvidence=evidence});
                    var unknown=gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());
                    Check(unknown.Status==LiveOrderStatus.Unknown&&journal.RecoveryRequired&&string.IsNullOrEmpty(unknown.BrokerOrderNumber),"unidentified zero-SOR response is unresolved rather than declared rejected");
                    Check(unknown.ResponseEvidence.Slot1OrderNumber=="00008333"&&unknown.ResponseEvidence.Disposition==OrderResponseDisposition.Unknown,"possible exchange identity remains separate evidence for reconciliation");
                    Reject(()=>gateway.Submit(Buy("new-key"),credentials,Authorized(Utc(),credentials),Utc()),"changing the strategy key cannot bypass an unresolved response layout");
                    Check(wire.Sends==1,"unresolved modern or legacy identity does not produce an automatic second request");
                }
                var responsePath=Path.Combine(directory,"rejected-response-diagnostics.xml");
                using(var journal=new LiveOrderJournal(responsePath))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    var slots=new object[]{"0","0","0","9002","1","0","0","0"};
                    var diagnostic=new IndiDiagnosticSnapshot{ErrorState=0,ErrorCode="INPUT37",Message="주문방식 확인 "+credentials.Account+" "+credentials.Password};
                    var evidence=LiveOrderResponseParser.Read(7,index=>slots[index],diagnostic,OrderResponseLayout.Modern,new[]{credentials.Account,credentials.Password});
                    wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,ResponseEvidence=evidence});
                    var rejected=gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());
                    Check(rejected.Status==LiveOrderStatus.Rejected&&!journal.RecoveryRequired,"identified all-zero venue response is retained as explicit rejection");
                    Check(rejected.ResponseEvidence.SafeDiagnostic.Contains("INPUT37")&&rejected.ResponseEvidence.SafeDiagnostic.Contains("주문방식"),"broker response code and redacted explanation survive the journal transition");
                    var responseXml=File.ReadAllText(responsePath);
                    Check(!responseXml.Contains(credentials.Account)&&!responseXml.Contains(credentials.Password)&&responseXml.Contains("ResponseEvidence"),"durable response diagnostics contain no account credentials");
                }
                using(var journal=new LiveOrderJournal(responsePath)) {
                    var rejected=journal.Snapshot.Orders.Single();
                    Check(rejected.Status==LiveOrderStatus.Rejected&&rejected.ResponseEvidence.Layout==OrderResponseLayout.Modern
                        &&rejected.ResponseEvidence.SafeDiagnostic.Contains("INPUT37"),"restart preserves the identified response and its safe rejection diagnostic");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"malformed-response-evidence.xml"))) {
                    var order=journal.Prepare(Buy(),Utc());journal.MarkDispatching(order.Id,Utc());journal.MarkRequestAccepted(order.Id,7,Utc());
                    var evidence=new OrderResponseEvidence{RequestId=7,Layout=OrderResponseLayout.Unknown,Disposition=OrderResponseDisposition.Accepted,BrokerOrderNumber="123",Slot0OrderNumber="123",SafeDiagnostic="safe"};
                    Reject(()=>journal.RecordAcknowledgement(order.Id,7,"123",false,Utc(),evidence),"unknown schema cannot be forged into an accepted journal response");
                    evidence.Layout=OrderResponseLayout.Legacy;evidence.SafeDiagnostic="line1\nline2";
                    Reject(()=>journal.RecordAcknowledgement(order.Id,7,"123",false,Utc(),evidence),"journal refuses unformatted multiline response diagnostics");
                    evidence.SafeDiagnostic="safe";evidence.RequestId=8;
                    Reject(()=>journal.RecordAcknowledgement(order.Id,7,"123",false,Utc(),evidence),"response evidence must match the actual accepted request identifier");
                    evidence.RequestId=7;evidence.Slot0OrderNumber="456";
                    Reject(()=>journal.RecordAcknowledgement(order.Id,7,"123",false,Utc(),evidence),"accepted canonical order identity must be present in the selected layout's exchange slots");
                    Check(journal.Snapshot.Orders.Single().Status==LiveOrderStatus.AwaitingAcknowledgement&&journal.Snapshot.Orders.Single().ResponseEvidence==null,"invalid response evidence cannot partially replace durable pending state");
                }
                var notSentPath=Path.Combine(directory,"not-sent-persisted.xml");
                using(var journal=new LiveOrderJournal(notSentPath))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    int notifications=0;gateway.OrderChanged+=order=>{if(order.Status==LiveOrderStatus.NotSent)notifications++;};
                    wire.BeforeSend=()=>{throw new InvalidOperationException("SECRET-before-request");};
                    string message="";try{gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());}catch(LiveOrderNotSentException error){message=error.Message;}
                    Check(wire.Sends==0&&notifications==1&&!message.Contains("SECRET"),"pre-entry preparation errors publish one safe not-sent change without provider text");
                    Check(!journal.RecoveryRequired&&journal.Snapshot.Orders[0].ClientKey.Length<=128,"generated audit key stays bounded and cannot require broker recovery");
                }
                using(var journal=new LiveOrderJournal(notSentPath))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    Check(!journal.RecoveryRequired&&journal.Snapshot.Orders[0].Status==LiveOrderStatus.NotSent&&journal.Snapshot.Orders[0].OriginalClientKey=="buy-a","proven-unsent audit remains terminal and retains its original strategy key after restart");
                    wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0011810"});
                    Check(gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc()).Status==LiveOrderStatus.Working&&wire.Sends==1,"a new explicit authorization can retry the audited unsent strategy key after restart");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"invalidate-reentry.xml")))using(var wire=new FakeWire())using(var gateway=new LiveOrderGateway(journal,wire,()=>Utc())) {
                    int notifications=0;wire.DuringSend=()=>wire.Emit(new LiveOrderAcknowledgement{RequestId=7,BrokerOrderNumber="0011901"});
                    gateway.Submit(Buy(),credentials,Authorized(Utc(),credentials),Utc());
                    gateway.OrderChanged+=order=>{notifications++;if(order.Status==LiveOrderStatus.Unknown)gateway.Invalidate(Utc());};
                    gateway.Invalidate(Utc());gateway.Invalidate(Utc());
                    Check(journal.RecoveryRequired&&notifications==1,"unknown-state notification cannot recursively invalidate itself or publish duplicate unknown transitions");
                }
                using(var journal=new LiveOrderJournal(Path.Combine(directory,"rejection-events.xml"))) {
                    var original=Accepted(journal,Buy(),"001201",Utc());
                    var cancellation=Accepted(journal,Modify(LiveOrderKind.Cancel,"cancel-rejected","001201"),"001202",Utc(1),8);
                    journal.RecordBrokerRejection("001202","20261112","99",Utc(2));
                    Check(journal.Snapshot.Orders.Single(o=>o.Id==cancellation.Id).Status==LiveOrderStatus.Rejected&&journal.ReservedBuyAmount==100000,"explicit cancel rejection closes only the cancellation and preserves the original reserve");
                    journal.RecordBrokerRejection("001201","20261112","09",Utc(3));
                    Check(journal.Snapshot.Orders.Single(o=>o.Id==original.Id).Status==LiveOrderStatus.Rejected&&journal.ReservedBuyAmount==0,"explicit new-order rejection is distinct from an execution or cancellation");
                    Reject(()=>journal.RecordBrokerRejection("001201","20261112","03",Utc(4)),"execution event cannot be relabeled as an order rejection");
                }
                using(var text=new StringWriter()) {
                    var token=new LiveTradingSession();token.Authorize(credentials.Account,Utc());var serializer=new System.Xml.Serialization.XmlSerializer(typeof(LiveTradingSession));serializer.Serialize(text,token);
                    using(var reader=new StringReader(text.ToString()))Check(!((LiveTradingSession)serializer.Deserialize(reader)).Active&&!text.ToString().Contains(credentials.Account),"serializing an approval token cannot persist activation or account credentials");
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
                TerminalRecoveryTests(directory);
                return checks;
            }finally{Directory.Delete(directory,true);}
        }
    }
}
