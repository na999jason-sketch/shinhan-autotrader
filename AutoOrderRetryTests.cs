using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShinhanNativeTrader {
    public static class AutoOrderRetryTests {
        static readonly DateTime Now=new DateTime(2026,11,13,1,0,0,DateTimeKind.Utc);
        const string Account="12345678901",OtherAccount="12345678902",Code="005930";
        const string RootId="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        static int checks;
        static void Check(bool value,string text){checks++;if(!value)throw new Exception("AutoOrderRetryTests: "+text);}
        static string Day {get{return MarketTime.ToKorea(Now).ToString("yyyyMMdd",CultureInfo.InvariantCulture);}}
        static string Key(LiveOrderKind kind){return (kind==LiveOrderKind.Buy?"entry-":"exit-")+Day+"-"+Code;}
        static string AccountKey(string account) {
            using(var hash=SHA256.Create())return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes("ShinhanNativeTrader/real-account/v1/"+account))
                .Select(value=>value.ToString("x2",CultureInfo.InvariantCulture)));
        }
        static LiveOrderRecord Rejected(LiveOrderKind side=LiveOrderKind.Sell) {
            var evidence=LiveOrderResponseParser.Read(71,index=>new[]{"0","0","0","0","0","synthetic rejection","0","0"}[index],
                new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new string[0]);
            return new LiveOrderRecord{Id=RootId,ClientKey=Key(side),Code=Code,Kind=side,TradeKind=side,Quantity=3,LimitPrice=70000,
                Status=LiveOrderStatus.Rejected,RequestId=71,TradingDate=Day,CreatedUtc=Now.AddMinutes(-10),UpdatedUtc=Now.AddSeconds(-31),ResponseEvidence=evidence};
        }
        static LiveOrderJournalState State(LiveOrderRecord root=null) {
            var state=new LiveOrderJournalState{AccountFingerprint=Convert.ToBase64String(new byte[32])};
            if(root!=null)state.Orders.Add(root);return state;
        }
        static BrokerOrdersSnapshot Snapshot(DateTime? started=null,DateTime? received=null,string account=Account) {
            var value=new BrokerOrdersSnapshot{TradingDate=Day};
            value.BindRequestScope(started??Now.AddSeconds(-2),received??Now.AddSeconds(-1),true,account);return value;
        }
        static AutoRetryDecision Evaluate(LiveOrderJournalState state,BrokerOrdersSnapshot snapshot=null,LiveOrderKind side=LiveOrderKind.Sell,DateTime? utc=null) {
            return AutoOrderRetry.Evaluate(state,Key(side),Code,side,AccountKey(Account),snapshot??Snapshot(),utc??Now);
        }
        public static int Run() {
            checks=0;
            foreach(var side in new[]{LiveOrderKind.Buy,LiveOrderKind.Sell}) {
                var root=Rejected(side);var state=State(root);var original=state.Orders.Single();
                var decision=Evaluate(state,side:side);
                Check(decision.State==AutoRetryState.Eligible&&decision.ClientKey=="auto-retry-"+RootId+"-1"&&decision.PreviousOrderId==RootId&&decision.Attempt==1,
                    "a verified clean rejection can plan a separate automatic buy/sell preflight key");
                Check(state.Orders.Count==1&&ReferenceEquals(state.Orders[0],original)&&state.Executions.Count==0,
                    "planning a retry does not modify history create an execution or send any order");
            }
            Check(Evaluate(State()).State==AutoRetryState.NotNeeded,"a first strategy signal retains the original audit key");
            var rejected=Rejected();var journal=State(rejected);rejected.UpdatedUtc=Now.AddSeconds(-29);
            var wait=Evaluate(journal);
            Check(wait.State==AutoRetryState.Waiting&&wait.NextAttemptUtc==Now.AddSeconds(1)&&wait.ClientKey==null,"the first cooldown expires after thirty seconds without pre-creating a key");
            rejected.UpdatedUtc=Now.AddSeconds(-31);
            Check(Evaluate(journal,Snapshot(Now.AddSeconds(-40),Now.AddSeconds(-39))).State==AutoRetryState.RefreshRequired,
                "a pre-rejection snapshot cannot prove current account state");
            Check(Evaluate(journal,Snapshot(Now.AddSeconds(-7),Now.AddSeconds(-6))).State==AutoRetryState.RefreshRequired,
                "a stale account response cannot trigger retry planning");
            Check(Evaluate(journal,Snapshot(account:OtherAccount)).State==AutoRetryState.RefreshRequired,
                "an independently verified different-account snapshot cannot trigger a retry");
            var unproved=new BrokerOrdersSnapshot{TradingDate=Day,RequestStartedUtc=Now.AddSeconds(-2),ReceivedUtc=Now.AddSeconds(-1)};
            Check(Evaluate(journal,unproved).State==AutoRetryState.RefreshRequired,"a fabricated unbound orders snapshot does not satisfy retry refresh");
            var open=Snapshot();open.Orders.Add(new BrokerOrderRow{Code=Code,OrderNumber="123",OpenQuantity=1});
            Check(Evaluate(journal,open).State==AutoRetryState.Blocked,"a broker working order for the symbol blocks automated rejection retry");
            var legacy=Rejected();legacy.ResponseEvidence=null;
            Check(Evaluate(State(legacy)).State==AutoRetryState.Blocked,"absence in an incomplete snapshot cannot turn a legacy no-evidence record into proven rejection");
            foreach(var status in new[]{LiveOrderStatus.Unknown,LiveOrderStatus.Working,LiveOrderStatus.PartiallyFilled,LiveOrderStatus.Filled,
                LiveOrderStatus.Cancelled,LiveOrderStatus.Prepared,LiveOrderStatus.AwaitingAcknowledgement,LiveOrderStatus.Dispatching}) {
                var value=Rejected();value.Status=status;
                Check(Evaluate(State(value)).State==AutoRetryState.Blocked,"accepted uncertain or already completed records never auto replay: "+status);
            }
            foreach(var mutate in new Action<LiveOrderRecord>[] {
                value=>value.BrokerOrderNumber="123",value=>value.FilledQuantity=1,value=>value.CancelledQuantity=1,
                value=>value.ResponseEvidence.ParseFailed=true,
                value=>value.ResponseEvidence.Disposition=OrderResponseDisposition.Unknown,value=>value.ResponseEvidence.Layout=OrderResponseLayout.Unknown,
                value=>value.ResponseEvidence.Slot1OrderNumber="123",value=>value.ResponseEvidence.Slot3OrderNumber="not-reference",
                value=>value.ResponseEvidence.Slot2OrderNumber="",value=>value.RequestId=72,value=>value.Kind=LiveOrderKind.Cancel,
                value=>value.OriginalBrokerOrderNumber="333",value=>value.UpdatedUtc=Now.AddSeconds(1)
            }) {
                var value=Rejected();mutate(value);
                Check(Evaluate(State(value)).State==AutoRetryState.Blocked,"ambiguous rejected evidence cannot be blindly replayed");
            }
            var explicitRejection=Rejected();explicitRejection.ResponseEvidence.ProviderError=true;
            Check(Evaluate(State(explicitRejection)).State==AutoRetryState.Eligible,
                "provider error plus explicit zero venue identifiers may be reevaluated only after fresh healthy account proof");
            explicitRejection.ResponseEvidence.Slot3OrderNumber="000789";
            Check(Evaluate(State(explicitRejection)).State==AutoRetryState.Eligible,"positive ORC reference is not an accepted venue identifier");
            explicitRejection.ResponseEvidence.Slot1OrderNumber="";
            Check(Evaluate(State(explicitRejection)).State==AutoRetryState.Blocked,"provider error with a missing venue field is not automatic retry evidence");
            var legacyLayout=Rejected();legacyLayout.ResponseEvidence=LiveOrderResponseParser.Read(71,index=>new[]{"0","000789","1","synthetic rejection","0","0"}[index],
                new IndiDiagnosticSnapshot{ErrorState=1},OrderResponseLayout.Legacy,new string[0]);
            Check(Evaluate(State(legacyLayout)).State==AutoRetryState.Eligible,"identified legacy zero order with numeric ORC remains a definite rejection");
            legacyLayout.ResponseEvidence.Slot0OrderNumber="";
            Check(Evaluate(State(legacyLayout)).State==AutoRetryState.Blocked,"missing legacy order identifier is not inferred as zero from provider error");
            journal=State(Rejected());var other=Rejected(LiveOrderKind.Buy);other.Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";other.Code="000660";other.Status=LiveOrderStatus.Unknown;journal.Orders.Add(other);
            Check(Evaluate(journal).State==AutoRetryState.Blocked,"any unresolved unknown journal order keeps the recovery barrier in place");
            journal=State(Rejected());journal.Executions.Add(new LiveExecutionRecord{OrderId=RootId,ExecutionId="F1",Quantity=1,Price=70000});
            Check(Evaluate(journal).State==AutoRetryState.Blocked,"an execution attached to rejected history prevents automatic replay");
            journal=State(Rejected());
            var first=Rejected();first.Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";first.ClientKey="auto-retry-"+RootId+"-1";first.UpdatedUtc=Now.AddSeconds(-59);journal.Orders.Add(first);
            var latestRoot=journal.Orders[0];latestRoot.UpdatedUtc=Now.AddMinutes(-5);
            var second=Evaluate(journal);
            Check(second.State==AutoRetryState.Waiting&&second.Attempt==2&&second.NextAttemptUtc==Now.AddSeconds(1),"the second retry uses a sixty-second rejection cooldown");
            first.UpdatedUtc=Now.AddSeconds(-61);second=Evaluate(journal);
            Check(second.State==AutoRetryState.Eligible&&second.ClientKey=="auto-retry-"+RootId+"-2","durable retry records select a new second-attempt key after restart");
            first.UpdatedUtc=Now.AddSeconds(-181);
            var third=Rejected();third.Id="cccccccccccccccccccccccccccccccc";third.ClientKey="retry-"+RootId+"-manual";third.UpdatedUtc=Now.AddSeconds(-119);journal.Orders.Add(third);
            var last=Evaluate(journal);
            Check(last.State==AutoRetryState.Waiting&&last.Attempt==3&&last.NextAttemptUtc==Now.AddSeconds(1),"manual retries share the same daily cap and third cooldown is two minutes");
            third.UpdatedUtc=Now.AddSeconds(-121);last=Evaluate(journal);
            Check(last.State==AutoRetryState.Eligible&&last.ClientKey=="auto-retry-"+RootId+"-3","third planned attempt is deterministic and separately auditable");
            var fourth=Rejected();fourth.Id="dddddddddddddddddddddddddddddddd";fourth.ClientKey=last.ClientKey;journal.Orders.Add(fourth);
            Check(Evaluate(journal).State==AutoRetryState.Blocked,"three durable retries exhaust the code-side-day cap");
            fourth.Status=LiveOrderStatus.NotSent;fourth.OriginalClientKey=fourth.ClientKey;fourth.ClientKey="not-sent/"+fourth.Id;
            Check(Evaluate(journal).State==AutoRetryState.Eligible,"a proven unsent audit does not consume a broker retry or occupy the freed audit key");
            Check(AutoOrderRetry.Cooldown(1)==TimeSpan.FromSeconds(30)&&AutoOrderRetry.Cooldown(2)==TimeSpan.FromSeconds(60)
                &&AutoOrderRetry.Cooldown(3)==TimeSpan.FromSeconds(120),"retry timing is bounded and independent of processor clock resolution");
            LegacyReevaluationChecks();
            return checks;
        }
        static void LegacyReevaluationChecks() {
            var root=Rejected();root.Code="305720";root.ClientKey="exit-"+Day+"-"+root.Code;root.ResponseEvidence=null;root.UpdatedUtc=Now.AddMinutes(-5);
            var state=State(root);
            state.LegacySellRechecks.Add(new LegacySellRecheckGrant{RootOrderId=root.Id,AccountFingerprint=state.AccountFingerprint,
                TradingDate=Day,Source=LiveOrderJournal.LegacySellRecheckSource,GrantedUtc=Now.AddSeconds(-5)});
            Func<BrokerOrdersSnapshot,AutoRetryDecision> evaluate=snapshot=>AutoOrderRetry.Evaluate(state,root.ClientKey,root.Code,LiveOrderKind.Sell,AccountKey(Account),snapshot,Now);
            var decision=evaluate(Snapshot());
            Check(decision.State==AutoRetryState.Eligible&&decision.LegacyReevaluation&&decision.ClientKey=="auto-legacy-"+RootId+"-1",
                "a scoped operator-confirmed old sell can plan one separately labelled legacy reevaluation");
            Check(root.ResponseEvidence==null&&root.Status==LiveOrderStatus.Rejected&&state.Orders.Count==1&&state.LegacySellRechecks.Count==1,
                "operator confirmation is not converted into synthetic broker rejection or an execution");
            Check(evaluate(Snapshot(Now.AddSeconds(-7),Now.AddSeconds(-6))).State==AutoRetryState.RefreshRequired,
                "legacy reevaluation requires a fresh request begun after its authorization grant");
            var grant=state.LegacySellRechecks[0];grant.AccountFingerprint="different";
            Check(evaluate(Snapshot()).State==AutoRetryState.Blocked,"a legacy grant for a different account cannot authorize replay");grant.AccountFingerprint=state.AccountFingerprint;
            grant.TradingDate="20261112";
            Check(evaluate(Snapshot()).State==AutoRetryState.Blocked,"a previous-day legacy grant cannot authorize current trading");grant.TradingDate=Day;
            grant.Source="not a user confirmation";
            Check(evaluate(Snapshot()).State==AutoRetryState.Blocked,"arbitrary source labels cannot become operator confirmation");grant.Source=LiveOrderJournal.LegacySellRecheckSource;
            root.Kind=LiveOrderKind.Buy;root.TradeKind=LiveOrderKind.Buy;root.ClientKey="entry-"+Day+"-"+root.Code;
            Check(AutoOrderRetry.Evaluate(state,root.ClientKey,root.Code,LiveOrderKind.Buy,AccountKey(Account),Snapshot(),Now).State==AutoRetryState.Blocked,
                "legacy buy history is not covered by an operator-confirmed sell migration");
            root.Kind=LiveOrderKind.Sell;root.TradeKind=LiveOrderKind.Sell;root.ClientKey="exit-"+Day+"-"+root.Code;
            var child=Rejected();child.Code=root.Code;child.ClientKey=decision.ClientKey;child.Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            child.UpdatedUtc=Now.AddSeconds(-61);state.Orders.Add(child);
            var second=evaluate(Snapshot());
            Check(second.State==AutoRetryState.Eligible&&!second.LegacyReevaluation&&second.Attempt==2&&second.ClientKey=="auto-retry-"+RootId+"-2",
                "after one legacy reevaluation only a newly proven rejected child can enable ordinary automatic retry");
            child.ResponseEvidence=null;
            Check(evaluate(Snapshot()).State==AutoRetryState.Blocked,"a legacy reevaluation with missing reply evidence cannot be automatically repeated");
            child.Status=LiveOrderStatus.Unknown;
            Check(evaluate(Snapshot()).State==AutoRetryState.Blocked,"uncertain legacy reevaluation always returns to the recovery barrier");
        }
    }
}
