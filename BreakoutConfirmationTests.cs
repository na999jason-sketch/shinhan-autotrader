using System;
using System.Globalization;

namespace ShinhanNativeTrader {
    public static class BreakoutConfirmationTests {
        static int checks;
        static readonly DateTime Now=new DateTime(2026,11,17,1,0,0,DateTimeKind.Utc);
        static void Check(bool value,string message){checks++;if(!value)throw new Exception("BreakoutConfirmationTests: "+message);}
        static PremarketReference Reference(StrategySettings settings=null,string code="005930",DateTime? target=null) {
            return new PremarketReference(new Stock{Code=code,Name="SYNTHETIC BREAKOUT",Market="0"},target??MarketTime.ToKorea(Now).Date,
                "20261116",10000,1000,9900,9800,9950,25,1,settings??new StrategySettings());
        }
        static RealtimeQuote Quote(int second,decimal price=10050,decimal? volume=null,string code="005930") {
            var utc=Now.AddSeconds(second);
            return new RealtimeQuote{Code=code,Clock=MarketTime.ToKorea(utc).ToString("HHmmss",CultureInfo.InvariantCulture),
                MarketCode="1",Price=price,CumulativeVolume=volume??2000+second,ReceivedUtc=utc,SequenceConfirmed=true};
        }
        static Candidate Evaluate(BreakoutConfirmationTracker tracker,PremarketReference reference,RealtimeQuote quote,StrategySettings settings=null,DateTime? utc=null) {
            string status;return tracker.Evaluate(reference,quote,settings??new StrategySettings(),utc??quote.ReceivedUtc,out status);
        }
        static RealtimeQuote Warm(BreakoutConfirmationTracker tracker,PremarketReference reference,StrategySettings settings=null,int start=0,int duration=60,int step=10) {
            RealtimeQuote latest=null;
            for(int second=start;second<=start+duration;second+=step) {
                latest=Quote(second,code:reference.Code);tracker.Observe(reference,latest,settings??new StrategySettings(),latest.ReceivedUtc);
            }
            return latest;
        }
        public static int Run() {
            checks=0;var settings=new StrategySettings();var reference=Reference(settings);var tracker=new BreakoutConfirmationTracker();
            var first=Quote(0);string status;
            Check(PremarketResearch.EvaluateLive(reference,first,settings,Now)!=null,"the test starts with an otherwise qualified observed breakout");
            Check(Evaluate(tracker,reference,first)==null,"a qualified current quote without observed history cannot create entry confirmation");
            tracker.Observe(reference,first,settings,Now);
            Check(Evaluate(tracker,reference,first)==null,"the first eligible SC trade starts observation rather than buying immediately");
            tracker.Evaluate(reference,first,settings,Now,out status);
            Check(status.Contains("0/60"),"the strategy explains its initial confirmation duration");
            var fifty=Warm(tracker,reference,duration:50);
            Check(Evaluate(tracker,reference,fifty)==null,"fifty observed seconds do not satisfy the default sixty-second hold");
            Check(Evaluate(tracker,reference,fifty,utc:Now.AddSeconds(60))==null,"scheduler time cannot mature a cached fifty-second sequence");
            var sixty=Quote(60);tracker.Observe(reference,sixty,settings,sixty.ReceivedUtc);
            var candidate=Evaluate(tracker,reference,sixty);
            Check(candidate!=null&&candidate.Price==10050&&candidate.SignalDate=="20261117"
                &&candidate.Reason.Contains("SC 돌파 유지 60초"),"a continuously observed and actively traded breakout becomes actionable at the exact hold boundary");
            tracker.Evaluate(reference,sixty,settings,sixty.ReceivedUtc,out status);
            Check(status.Contains("돌파 유지 확인 완료"),"the confirmed signal explains the completed strategy check");
            Check(Evaluate(tracker,reference,sixty,utc:sixty.ReceivedUtc.AddSeconds(15))!=null,"the latest evidence remains usable at the exact fifteen-second freshness boundary");
            Check(Evaluate(tracker,reference,sixty,utc:sixty.ReceivedUtc.AddSeconds(16))==null,"a matured but no longer fresh observed sequence cannot authorize a new entry");

            tracker=new BreakoutConfirmationTracker();Warm(tracker,reference,duration:50);
            var dip=Quote(55,price:10000);tracker.Observe(reference,dip,settings,dip.ReceivedUtc);
            var afterDip=Warm(tracker,reference,start:60,duration:50);
            Check(Evaluate(tracker,reference,afterDip)==null,"touching the breakout line resets the episode instead of carrying forward pre-dip time");
            var recovered=Quote(120);tracker.Observe(reference,recovered,settings,recovered.ReceivedUtc);
            Check(Evaluate(tracker,reference,recovered)!=null,"a full new continuous interval can qualify after a failed breakout");
            var overheated=Quote(125,price:10201);tracker.Observe(reference,overheated,settings,overheated.ReceivedUtc);
            Check(Evaluate(tracker,reference,Quote(130))==null,"a price exceeding the chase cap destroys the earlier confirmation");

            tracker=new BreakoutConfirmationTracker();var atGap=Warm(tracker,reference,step:15);
            Check(Evaluate(tracker,reference,atGap)!=null,"actual trades spaced exactly fifteen seconds can prove the full interval");
            var gap=Quote(76);tracker.Observe(reference,gap,settings,gap.ReceivedUtc);
            Check(Evaluate(tracker,reference,gap)==null,"a sixteen-second trade gap restarts confirmation even when the quote and volume remain eligible");
            tracker=new BreakoutConfirmationTracker();tracker.Observe(reference,Quote(0),settings,Now);
            var receiptJump=Quote(1);receiptJump.ReceivedUtc=Now.AddSeconds(16);
            tracker.Observe(reference,receiptJump,settings,receiptJump.ReceivedUtc);
            tracker.Evaluate(reference,receiptJump,settings,receiptJump.ReceivedUtc,out status);
            Check(status.Contains("0/60"),"a long receipt gap is detected independently of a near-consecutive broker clock");
            tracker=new BreakoutConfirmationTracker();tracker.Observe(reference,Quote(0),settings,Now);
            var clockJump=Quote(16);clockJump.ReceivedUtc=Now.AddSeconds(1);
            tracker.Observe(reference,clockJump,settings,clockJump.ReceivedUtc);
            tracker.Evaluate(reference,clockJump,settings,clockJump.ReceivedUtc,out status);
            Check(status.Contains("0/60"),"a large broker-clock gap cannot be hidden by closely packed callback receipts");

            foreach(var mutate in new Action<RealtimeQuote>[] {
                quote=>quote.Clock="095959",quote=>quote.CumulativeVolume=1999,quote=>quote.SequenceConfirmed=false,
                quote=>quote.MarketCode="2",quote=>quote.Price=9999,quote=>quote.Price=10201,
                quote=>quote.Clock="bad",quote=>quote.CumulativeVolume=2000.5m
            }) {
                tracker=new BreakoutConfirmationTracker();Warm(tracker,reference);
                var invalid=Quote(65);mutate(invalid);tracker.Observe(reference,invalid,settings,invalid.ReceivedUtc);
                var again=Quote(70);tracker.Observe(reference,again,settings,again.ReceivedUtc);
                Check(Evaluate(tracker,reference,again)==null,"invalid, regressed, nonregular, unconfirmed or ineligible SC resets a previously mature episode");
            }
            tracker=new BreakoutConfirmationTracker();Warm(tracker,reference);
            var future=Quote(65);tracker.Observe(reference,future,settings,Now.AddSeconds(64));
            var following=Quote(66);tracker.Observe(reference,following,settings,following.ReceivedUtc);
            Check(Evaluate(tracker,reference,following)==null,"a future-dated callback cannot preserve prior confirmation");
            tracker=new BreakoutConfirmationTracker();Warm(tracker,reference);
            var badZone=Quote(65);badZone.ReceivedUtc=DateTime.SpecifyKind(badZone.ReceivedUtc,DateTimeKind.Unspecified);
            tracker.Observe(reference,badZone,settings,Now.AddSeconds(65));
            following=Quote(66);tracker.Observe(reference,following,settings,following.ReceivedUtc);
            Check(Evaluate(tracker,reference,following)==null,"receipt timezone ambiguity invalidates the strategy's observation episode");
            tracker=new BreakoutConfirmationTracker();Warm(tracker,reference);
            var oldClock=Quote(65);oldClock.Clock="100000";tracker.Observe(reference,oldClock,settings,oldClock.ReceivedUtc);
            following=Quote(66);tracker.Observe(reference,following,settings,following.ReceivedUtc);
            Check(Evaluate(tracker,reference,following)==null,"a newly received callback cannot disguise a stale broker trade clock");

            tracker=new BreakoutConfirmationTracker();tracker.Observe(reference,Quote(0),settings,Now);
            for(int second=10;second<=60;second+=10) {
                var noVolume=Quote(second,volume:2000);tracker.Observe(reference,noVolume,settings,noVolume.ReceivedUtc);
            }
            Check(Evaluate(tracker,reference,Quote(60,volume:2000))==null,"clock-only progress without newly traded volume cannot confirm a breakout");
            tracker=new BreakoutConfirmationTracker();tracker.Observe(reference,Quote(0),settings,Now);
            var replay=Quote(0);replay.ReceivedUtc=Now.AddSeconds(10);tracker.Observe(reference,replay,settings,replay.ReceivedUtc);
            Check(Evaluate(tracker,reference,replay)==null,"a repeated clock and volume callback neither advances nor refreshes an episode");
            tracker=new BreakoutConfirmationTracker();Warm(tracker,reference);
            var conflicting=Quote(60,price:10060);tracker.Observe(reference,conflicting,settings,conflicting.ReceivedUtc);
            Check(Evaluate(tracker,reference,sixty)==null,"a changed price with a repeated clock and volume invalidates contradictory trade identity");

            tracker=new BreakoutConfirmationTracker();var protectedQuote=Warm(tracker,reference);
            var snapshot=protectedQuote.Copy();protectedQuote.Price=10060;
            Check(Evaluate(tracker,reference,protectedQuote)==null&&Evaluate(tracker,reference,snapshot)!=null,
                "caller mutation cannot replace the tracker-owned copied quote evidence");
            protectedQuote=snapshot.Copy();protectedQuote.ClockShape="mutated";
            Check(Evaluate(tracker,reference,protectedQuote)==null,"latest quote binding includes the original source shape metadata");
            var replacement=Reference(settings);
            Check(Evaluate(tracker,replacement,snapshot)==null,"recreated research cannot reuse an earlier reference's observation interval");
            tracker.Observe(replacement,snapshot,settings,snapshot.ReceivedUtc);
            Check(Evaluate(tracker,replacement,snapshot)==null,"recreated research begins its own full confirmation interval");
            var changed=settings.Copy();changed.BreakoutConfirmationSeconds=30;
            Check(Evaluate(tracker,reference,snapshot,changed)==null,"a different confirmation setting cannot inherit old entry evidence");
            tracker=new BreakoutConfirmationTracker();var shortReference=Reference(changed);
            var shorter=Warm(tracker,shortReference,changed,duration:30);
            Check(Evaluate(tracker,shortReference,shorter,changed)!=null,"the configured thirty-second hold is honored only with matching new observations");
            var longSettings=settings.Copy();longSettings.BreakoutConfirmationSeconds=300;
            tracker=new BreakoutConfirmationTracker();var longReference=Reference(longSettings);
            var longer=Warm(tracker,longReference,longSettings,duration:290);
            Check(Evaluate(tracker,longReference,longer,longSettings)==null,"a longer configured hold does not silently use the sixty-second default");
            longer=Quote(300);tracker.Observe(longReference,longer,longSettings,longer.ReceivedUtc);
            Check(Evaluate(tracker,longReference,longer,longSettings)!=null,"the supported five-minute confirmation boundary is enforced exactly");

            tracker=new BreakoutConfirmationTracker();var a=Warm(tracker,reference);var other=Reference(code:"000660");
            Check(Evaluate(tracker,other,Quote(60,code:"000660"))==null,"a second stock cannot borrow the first stock's mature episode");
            var b=Warm(tracker,other);tracker.Remove(other.Code);
            Check(Evaluate(tracker,other,b)==null&&Evaluate(tracker,reference,a)!=null,"untracking one stock removes only its own observation evidence");
            tracker.Clear();Check(Evaluate(tracker,reference,a)==null,"reconnect or account reset clears every prior confirmation");
            tracker=new BreakoutConfirmationTracker();Warm(tracker,reference);
            var tomorrow=Quote(65);tomorrow.ReceivedUtc=tomorrow.ReceivedUtc.AddDays(1);
            tracker.Observe(reference,tomorrow,settings,tomorrow.ReceivedUtc);
            var tomorrowReference=Reference(target:MarketTime.ToKorea(tomorrow.ReceivedUtc).Date);
            Check(Evaluate(tracker,tomorrowReference,tomorrow)==null,"a new trading day cannot inherit yesterday's observed hold");
            bool rejected=false;try{tracker.Observe(reference,Quote(0),settings,DateTime.SpecifyKind(Now,DateTimeKind.Unspecified));}
            catch(ArgumentException){rejected=true;}Check(rejected,"ambiguous strategy evaluation clocks are rejected explicitly");
            return checks;
        }
    }
}
