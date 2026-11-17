using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace ShinhanNativeTrader {
    public static class PendingBuyPolicyTests {
        static readonly DateTime Now=new DateTime(2026,11,16,1,0,0,DateTimeKind.Utc);
        const string Account="12345678901",OtherAccount="12345678902",Code="005930",RootId="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string Fingerprint="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        static int checks;
        static void Check(bool value,string text){checks++;if(!value)throw new Exception("PendingBuyPolicyTests: "+text);}
        static string Day {get{return MarketTime.ToKorea(Now).ToString("yyyyMMdd",CultureInfo.InvariantCulture);}}
        static string Key(string number) {
            using(var sha=SHA256.Create())return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("ShinhanNativeTrader/real-account/v1/"+number))
                .Select(value=>value.ToString("x2",CultureInfo.InvariantCulture)));
        }
        static LiveOrderRecord Buy(int age=120,int filled=0) {
            var evidence=LiveOrderResponseParser.Read(71,index=>new[]{"0","000123","0","","1","","",""}[index],
                new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
            return new LiveOrderRecord{Id=RootId,ClientKey="entry-"+Day+"-"+Code,Code=Code,Kind=LiveOrderKind.Buy,TradeKind=LiveOrderKind.Buy,
                Quantity=10,FilledQuantity=filled,LimitPrice=70000,RequestId=71,BrokerOrderNumber="123",ResponseEvidence=evidence,
                Status=filled>0?LiveOrderStatus.PartiallyFilled:LiveOrderStatus.Working,TradingDate=Day,
                CreatedUtc=Now.AddSeconds(-age),UpdatedUtc=Now.AddSeconds(-10)};
        }
        static LiveOrderJournalState Journal(LiveOrderRecord order=null) {
            var value=new LiveOrderJournalState{AccountFingerprint=Fingerprint,LastBrokerChangeUtc=Now.AddSeconds(-10)};
            if(order!=null) {
                value.Orders.Add(order);
                if(order.FilledQuantity>0)value.Executions.Add(new LiveExecutionRecord{ExecutionId="F1",OrderId=order.Id,BrokerOrderNumber=order.BrokerOrderNumber,
                    Quantity=order.FilledQuantity,Price=order.LimitPrice,TradingDate=Day,ExecutedUtc=Now.AddSeconds(-10)});
            }
            return value;
        }
        static BrokerOrderRow Row(LiveOrderRecord order) {
            return new BrokerOrderRow{OrderNumber="000"+order.BrokerOrderNumber,Code=order.Code,SideCode="2",AmendCancelCode="0",ProductCode="01",CreditCode="00",
                Quantity=order.Quantity,FilledQuantity=order.FilledQuantity,OpenQuantity=order.Quantity-order.FilledQuantity-order.CancelledQuantity,
                ConfirmedQuantity=order.CancelledQuantity,LimitPrice=order.LimitPrice,ConfirmationCode="0",AverageFillPrice=order.FilledQuantity>0?order.LimitPrice:0};
        }
        static BrokerOrdersSnapshot Snapshot(LiveOrderRecord root,DateTime? started=null,DateTime? received=null,string account=Account) {
            var value=new BrokerOrdersSnapshot{TradingDate=Day};
            value.BindRequestScope(started??Now.AddSeconds(-2),received??Now.AddSeconds(-1),true,account);
            if(root!=null)value.Orders.Add(Row(root));return value;
        }
        static PendingBuyDecision Evaluate(LiveOrderJournalState journal,BrokerOrdersSnapshot snapshot=null,int timeout=120,DateTime? utc=null) {
            return PendingBuyPolicy.Evaluate(journal,snapshot??Snapshot(journal.Orders.FirstOrDefault()),Key(Account),utc??Now,timeout);
        }
        static void Mutation(Action<LiveOrderRecord> mutate,string message) {
            var root=Buy();mutate(root);Check(Evaluate(Journal(root)).State!=PendingBuyState.CancelEligible,message);
        }
        static LiveOrderJournalState Cancelled() {
            var root=Buy(600);root.Status=LiveOrderStatus.Cancelled;root.CancelledQuantity=root.Quantity;root.UpdatedUtc=Now.AddSeconds(-10);
            var journal=Journal(root);
            var cancellation=new LiveOrderRecord{Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",ClientKey=PendingBuyPolicy.CancelKeyPrefix+root.Id+"-1",
                Code=Code,Kind=LiveOrderKind.Cancel,TradeKind=LiveOrderKind.Buy,TradingDate=Day,Quantity=root.Quantity,LimitPrice=root.LimitPrice,
                OriginalBrokerOrderNumber=root.BrokerOrderNumber,BrokerOrderNumber="124",RequestId=72,CreatedUtc=Now.AddSeconds(-20),UpdatedUtc=Now.AddSeconds(-10),
                Status=LiveOrderStatus.Cancelled,ResponseEvidence=LiveOrderResponseParser.Read(72,index=>new[]{"0","124","0","","1","","",""}[index],
                    new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0])};
            journal.Orders.Add(cancellation);journal.ConfirmationIds.Add(Day+"|124|AA05|10");return journal;
        }
        static BrokerOrdersSnapshot CancelledSnapshot(LiveOrderJournalState journal) {
            var value=Snapshot(journal.Orders[0]);value.Orders[0].ConfirmedQuantity=0;return value;
        }
        static AutoRetryDecision Reentry(LiveOrderJournalState journal,BrokerOrdersSnapshot snapshot=null) {
            return PendingBuyPolicy.EvaluateReentry(journal,"entry-"+Day+"-"+Code,Code,Key(Account),snapshot??CancelledSnapshot(journal),Now);
        }
        public static int Run() {
            checks=0;
            var root=Buy();var journal=Journal(root);var snapshot=Snapshot(root);var decision=Evaluate(journal,snapshot);
            Check(decision.State==PendingBuyState.CancelEligible&&decision.RemainingQuantity==10&&decision.OriginalOrderId==RootId
                &&decision.OriginalBrokerOrderNumber=="123"&&decision.ClientKey==PendingBuyPolicy.CancelKeyPrefix+RootId+"-1",
                "an accepted local strategy buy reaches a deterministic one-time cancellation plan at exactly120 seconds");
            Check(decision.Intent.Kind==LiveOrderKind.Cancel&&decision.Intent.Quantity==10&&decision.Intent.Code==Code
                &&decision.Intent.LimitPrice==70000&&decision.Intent.OriginalBrokerOrderNumber=="123","the plan cancels only the original broker remainder");
            Check(journal.Orders.Count==1&&ReferenceEquals(journal.Orders[0],root)&&root.Status==LiveOrderStatus.Working
                &&root.CancelledQuantity==0&&root.FilledQuantity==0&&journal.Executions.Count==0&&snapshot.Orders[0].OpenQuantity==10,
                "planning neither changes the journal or reservations nor manufactures broker fills");
            Check(Evaluate(Journal(Buy(119))).State==PendingBuyState.Waiting,"119 seconds remains inside the default wait");
            Check(Evaluate(Journal(Buy(30)),timeout:30).State==PendingBuyState.CancelEligible,"the configured thirty-second minimum is honored");
            Check(Evaluate(Journal(Buy(29)),timeout:30).State==PendingBuyState.Waiting,"the configured timeout does not fire before its boundary");
            root=Buy(1800);Check(Evaluate(Journal(root),timeout:1800).State==PendingBuyState.CancelEligible,"the maximum configured thirty-minute boundary is honored");
            foreach(int invalid in new[]{0,29,1801}) {
                bool rejected=false;try{Evaluate(Journal(Buy()),timeout:invalid);}catch(ArgumentOutOfRangeException){rejected=true;}
                Check(rejected,"invalid timeout cannot silently alter cancellation timing");
            }
            root=Buy(3600);root.UpdatedUtc=Now.AddSeconds(-3);journal=Journal(root);journal.LastBrokerChangeUtc=root.UpdatedUtc;
            Check(Evaluate(journal).State==PendingBuyState.CancelEligible,"a restarted or partially refreshed record ages from CreatedUtc rather than UpdatedUtc");
            root=Buy(120,4);journal=Journal(root);decision=Evaluate(journal);
            Check(decision.State==PendingBuyState.CancelEligible&&decision.RemainingQuantity==6&&decision.Intent.Quantity==6
                &&root.FilledQuantity==4&&root.CancelledQuantity==0&&journal.Executions.Sum(value=>value.Quantity)==4,
                "a partial fill preserves the purchased position and cancels only the remaining six shares");
            journal.Executions[0].Quantity=3;Check(Evaluate(journal).State==PendingBuyState.Blocked,"unexplained broker fill totals require reconciliation before cancel planning");
            root=Buy(120,4);journal=Journal(root);snapshot=Snapshot(root);snapshot.Orders[0].FilledQuantity=5;snapshot.Orders[0].OpenQuantity=5;
            Check(Evaluate(journal,snapshot).State==PendingBuyState.Blocked,"a fill race cannot reuse an older local remaining quantity");
            root=Buy();journal=Journal(root);snapshot=Snapshot(root);snapshot.Orders[0].FilledQuantity=10;snapshot.Orders[0].OpenQuantity=0;
            Check(Evaluate(journal,snapshot).State==PendingBuyState.Blocked,"a broker full fill wins over the timeout cancellation plan");
            root=Buy();journal=Journal(root);
            Check(Evaluate(journal,Snapshot(root,account:OtherAccount)).State==PendingBuyState.RefreshRequired,"another account response cannot authorize cancellation");
            var unbound=new BrokerOrdersSnapshot{TradingDate=Day,RequestStartedUtc=Now.AddSeconds(-2),ReceivedUtc=Now.AddSeconds(-1)};unbound.Orders.Add(Row(root));
            Check(Evaluate(journal,unbound).State==PendingBuyState.RefreshRequired,"unbound snapshot rows are not account evidence");
            Check(Evaluate(journal,Snapshot(root,Now.AddSeconds(-7),Now.AddSeconds(-6))).State==PendingBuyState.RefreshRequired,"stale account evidence requests refresh");
            Check(Evaluate(journal,Snapshot(root,Now.AddSeconds(-11),Now.AddSeconds(-1))).State==PendingBuyState.RefreshRequired,"a request started before a fill or acknowledgement cannot authorize cancellation");
            Check(Evaluate(journal,Snapshot(root,Now.AddSeconds(-1),Now.AddSeconds(1))).State==PendingBuyState.RefreshRequired,"future broker response timestamps are rejected");
            snapshot=Snapshot(root);snapshot.TradingDate="20261113";
            Check(Evaluate(journal,snapshot).State==PendingBuyState.RefreshRequired,"previous-day broker scope cannot cancel a current-day order");
            Mutation(value=>value.TradingDate="20261113","previous-day journal orders are never resent or canceled by the intraday timer");
            Mutation(value=>value.Kind=LiveOrderKind.Sell,"a timer never cancels a sell order");
            Mutation(value=>value.TradeKind=LiveOrderKind.Sell,"an inconsistent trade side cannot become a buy cancellation");
            Mutation(value=>value.ClientKey="external-manual-buy","manual or imported orders have no strategy-owned lineage");
            Mutation(value=>value.ResponseEvidence=null,"missing acceptance evidence prevents cancellation planning");
            Mutation(value=>value.ResponseEvidence.ProviderError=true,"an accepted-looking response with a provider error is not acceptance proof");
            Mutation(value=>value.ResponseEvidence.ParseFailed=true,"response parse failures require reconciliation");
            Mutation(value=>value.ResponseEvidence.Disposition=OrderResponseDisposition.Unknown,"unknown results cannot be treated as working orders");
            Mutation(value=>value.ResponseEvidence.Slot1OrderNumber="124","the accepted order number must match every local identifier");
            Mutation(value=>value.RequestId=72,"the successful acknowledgement must correlate to its transport request");
            Mutation(value=>value.OriginalBrokerOrderNumber="456","an amendment cannot masquerade as a new strategy buy");
            foreach(var status in new[]{LiveOrderStatus.Unknown,LiveOrderStatus.AwaitingAcknowledgement,LiveOrderStatus.Dispatching,LiveOrderStatus.Prepared,
                LiveOrderStatus.AwaitingConfirmation,LiveOrderStatus.Filled,LiveOrderStatus.Cancelled,LiveOrderStatus.Rejected,LiveOrderStatus.Replaced,LiveOrderStatus.NotSent})
                Mutation(value=>value.Status=status,"only working or partially filled accepted buys can time out: "+status);
            foreach(var mutate in new Action<BrokerOrderRow>[] {
                value=>value.OrderNumber="124",value=>value.Code="000660",value=>value.SideCode="1",value=>value.CreditCode="03",value=>value.ProductCode="02",
                value=>value.AmendCancelCode="1",value=>value.OriginalOrderNumber="456",value=>value.Quantity=9,value=>value.LimitPrice=69900,
                value=>value.OpenQuantity=9,value=>value.ConfirmationCode="",value=>value.ConfirmationCode="2",value=>value.RejectName="거부",value=>value.AutoCancelledQuantity=1,
                value=>value.ConfirmedQuantity=1,value=>value.QueryProfile=BrokerOrderQueryProfile.ModernOrderState54
            }) {
                root=Buy();journal=Journal(root);snapshot=Snapshot(root);mutate(snapshot.Orders[0]);
                Check(Evaluate(journal,snapshot).State==PendingBuyState.Blocked,"inconsistent or ambiguous current broker row cannot authorize cancellation");
            }
            root=Buy();journal=Journal(root);snapshot=Snapshot(root);snapshot.Orders.Add(Row(root));
            Check(Evaluate(journal,snapshot).State==PendingBuyState.Blocked,"duplicate broker identity is not a cancellation proof");
            snapshot=Snapshot(root);snapshot.Orders.Add(new BrokerOrderRow{OrderNumber="321",OriginalOrderNumber="123"});
            Check(Evaluate(journal,snapshot).State==PendingBuyState.Blocked,"a broker amendment or cancellation already referencing the root prevents a second cancel");
            snapshot=Snapshot(root);snapshot.Orders[0].QueryProfile=BrokerOrderQueryProfile.ModernOrderState54;
            snapshot.Orders[0].QuerySchemaSha256=new string('a',64);snapshot.Orders[0].OrderMethodCode="1";
            snapshot.Orders[0].ConfirmationCode="1";snapshot.Orders[0].StatusName="접수";
            Check(Evaluate(journal,snapshot).State==PendingBuyState.CancelEligible,"recognized modern received status remains separate from legacy normal code");
            foreach(var status in new[]{LiveOrderStatus.Prepared,LiveOrderStatus.AwaitingAcknowledgement,LiveOrderStatus.AwaitingConfirmation,
                LiveOrderStatus.Unknown,LiveOrderStatus.Cancelled,LiveOrderStatus.Rejected,LiveOrderStatus.NotSent}) {
                root=Buy();journal=Journal(root);journal.Orders.Add(new LiveOrderRecord{Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",ClientKey=PendingBuyPolicy.CancelKeyPrefix+RootId+"-1",
                    Code=Code,Kind=LiveOrderKind.Cancel,TradeKind=LiveOrderKind.Buy,TradingDate=Day,OriginalBrokerOrderNumber="123",Status=status});
                Check(Evaluate(journal).State==PendingBuyState.Blocked,"a consumed durable cancellation key prevents every automatic replay, including unknown or unconfirmed cancellation: "+status);
            }
            root=Buy();journal=Journal(root);journal.Orders.Add(new LiveOrderRecord{Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",ClientKey="manual-amend-1",Code=Code,
                Kind=LiveOrderKind.Amend,TradeKind=LiveOrderKind.Buy,TradingDate=Day,OriginalBrokerOrderNumber="123",Status=LiveOrderStatus.AwaitingConfirmation});
            Check(Evaluate(journal).State==PendingBuyState.Blocked,"a live amendment must settle before the original remainder can be canceled");
            root=Buy(600);journal=Journal(root);journal.Orders.Add(new LiveOrderRecord{Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                ClientKey=PendingBuyPolicy.CancelKeyPrefix+RootId+"-1",Code=Code,Kind=LiveOrderKind.Cancel,TradeKind=LiveOrderKind.Buy,
                TradingDate=Day,OriginalBrokerOrderNumber="123",Status=LiveOrderStatus.AwaitingConfirmation});
            var second=Buy();second.Id="cccccccccccccccccccccccccccccccc";second.Code="000660";second.ClientKey="entry-"+Day+"-000660";
            second.BrokerOrderNumber="456";second.RequestId=72;second.ResponseEvidence=LiveOrderResponseParser.Read(72,index=>new[]{"0","456","0","","1","","",""}[index],
                new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);journal.Orders.Add(second);
            decision=Evaluate(journal,Snapshot(second,Now.AddSeconds(-7),Now.AddSeconds(-6)));
            Check(decision.State==PendingBuyState.RefreshRequired&&decision.OriginalOrderId==second.Id,
                "an older buy awaiting cancellation confirmation cannot mask the fresh-query request of another expired buy");
            decision=Evaluate(journal,Snapshot(second));
            Check(decision.State==PendingBuyState.CancelEligible&&decision.OriginalOrderId==second.Id&&decision.Intent.OriginalBrokerOrderNumber=="456",
                "a refreshed second expired buy remains eligible while the first buy waits for cancellation confirmation");
            root=Buy();journal=Journal(root);var other=Buy();other.Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";other.ClientKey="entry-"+Day+"-000660";
            other.Code="000660";other.Status=LiveOrderStatus.Unknown;journal.Orders.Add(other);
            Check(Evaluate(journal).State==PendingBuyState.Blocked,"an unknown order on another stock preserves the recovery barrier");
            journal=Cancelled();snapshot=CancelledSnapshot(journal);var reentry=Reentry(journal,snapshot);
            Check(reentry.State==AutoRetryState.Eligible&&reentry.ClientKey==PendingBuyPolicy.ReentryKeyPrefix+RootId+"-1"
                &&reentry.PreviousOrderId==RootId&&reentry.Attempt==1,"AA05-proven zero-fill full cancellation permits one separate current strategy evaluation");
            Check(journal.Orders.Count==2&&journal.Orders[0].FilledQuantity==0&&journal.Orders[0].CancelledQuantity==10&&journal.Executions.Count==0,
                "reentry planning neither creates a new buy nor rewrites the canceled order");
            Check(AutoOrderRetry.Evaluate(journal,"entry-"+Day+"-"+Code,Code,LiveOrderKind.Buy,Key(Account),snapshot,Now).ClientKey==reentry.ClientKey,
                "existing automatic entry verification obtains the same bounded cancellation reentry key");
            journal.ConfirmationIds.Clear();Check(Reentry(journal).State==AutoRetryState.Blocked,"cancellation status without the actual AA05 confirmation is not full-cancel proof");
            journal=Cancelled();journal.ConfirmationIds[0]=Day+"|124|QUERY|10";
            Check(Reentry(journal).State==AutoRetryState.Blocked,"an invented query confirmation cannot replace the actual AA05 confirmation");
            journal=Cancelled();journal.Orders[1].Status=LiveOrderStatus.AwaitingConfirmation;
            Check(Reentry(journal).State==AutoRetryState.Blocked,"an accepted cancel awaiting broker confirmation never permits reentry");
            journal=Cancelled();journal.Orders[0].Status=LiveOrderStatus.Working;journal.Orders[0].CancelledQuantity=0;journal.Orders[1].Status=LiveOrderStatus.AwaitingConfirmation;
            Check(AutoOrderRetry.Evaluate(journal,"entry-"+Day+"-"+Code,Code,LiveOrderKind.Buy,Key(Account),Snapshot(journal.Orders[0]),Now).State==AutoRetryState.Blocked,
                "cancel acknowledgement alone never bypasses the original working-order replay barrier");
            journal=Cancelled();journal.Orders[0].FilledQuantity=1;journal.Orders[0].CancelledQuantity=9;
            Check(Reentry(journal).State==AutoRetryState.Blocked,"a partially filled canceled buy leaves a real position and never reenters automatically");
            journal=Cancelled();snapshot=CancelledSnapshot(journal);snapshot.Orders[0].OpenQuantity=1;
            Check(Reentry(journal,snapshot).State==AutoRetryState.Blocked,"broker remaining quantity after local confirmation prevents reentry");
            journal=Cancelled();snapshot=CancelledSnapshot(journal);snapshot.Orders[0].FilledQuantity=1;
            Check(Reentry(journal,snapshot).State==AutoRetryState.Blocked,"a raced broker fill after cancellation prevents reentry");
            journal=Cancelled();snapshot=CancelledSnapshot(journal);snapshot.BindRequestScope(Now.AddSeconds(-8),Now.AddSeconds(-6),true,Account);
            Check(Reentry(journal,snapshot).State==AutoRetryState.RefreshRequired,"a canceled order still requires a fresh account response before strategy reentry");
            journal=Cancelled();snapshot=CancelledSnapshot(journal);snapshot.BindRequestScope(Now.AddSeconds(-2),Now.AddSeconds(-1),true,OtherAccount);
            Check(Reentry(journal,snapshot).State==AutoRetryState.RefreshRequired,"reentry cannot use another account snapshot");
            foreach(var status in new[]{LiveOrderStatus.Rejected,LiveOrderStatus.Filled,LiveOrderStatus.Cancelled,LiveOrderStatus.NotSent}) {
                journal=Cancelled();journal.Orders.Add(new LiveOrderRecord{Id="cccccccccccccccccccccccccccccccc",ClientKey=PendingBuyPolicy.ReentryKeyPrefix+RootId+"-1",
                    Kind=LiveOrderKind.Buy,TradeKind=LiveOrderKind.Buy,Code=Code,TradingDate=Day,Status=status});
                Check(Reentry(journal).State==AutoRetryState.Blocked,"one consumed reentry key prevents repeated new buys even when its later result is terminal: "+status);
            }
            journal=Cancelled();root=Buy();root.Id="cccccccccccccccccccccccccccccccc";root.ClientKey=PendingBuyPolicy.ReentryKeyPrefix+RootId+"-1";
            root.BrokerOrderNumber="222";root.RequestId=73;root.ResponseEvidence=LiveOrderResponseParser.Read(73,index=>new[]{"0","222","0","","1","","",""}[index],
                new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);journal.Orders.Add(root);
            Check(Evaluate(journal,Snapshot(root)).State==PendingBuyState.CancelEligible,"the one reentry buy also has a finite timeout and can cancel its own remaining quantity");
            var settings=new StrategySettings();Check(settings.BuyOrderTimeoutSeconds==120&&settings.Copy().BuyOrderTimeoutSeconds==120,
                "the strategy defaults and detached copy preserve the two-minute buy timeout");
            settings.BuyOrderTimeoutSeconds=300;Check(settings.Copy().BuyOrderTimeoutSeconds==300,"a configured timeout survives strategy copying");
            foreach(int invalid in new[]{29,1801}) {
                settings.BuyOrderTimeoutSeconds=invalid;bool rejected=false;try{settings.Validate();}catch(ArgumentException){rejected=true;}
                Check(rejected,"strategy validation enforces timeout bounds");
            }
            var serializer=new XmlSerializer(typeof(StrategySettings));
            using(var reader=new StringReader("<StrategySettings><StopLossPercent>6</StopLossPercent></StrategySettings>")) {
                var legacy=(StrategySettings)serializer.Deserialize(reader);legacy.Validate();
                Check(legacy.BuyOrderTimeoutSeconds==120,"old settings XML without the new field retains the timeout initializer");
            }
            return checks;
        }
    }
}
