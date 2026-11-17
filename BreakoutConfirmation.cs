using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShinhanNativeTrader {
    // Strategy evidence comes only from observed SC trades. Scheduler ticks,
    // cached quotes and duplicated callbacks cannot age a breakout into entry.
    public sealed class BreakoutConfirmationTracker {
        public const int MaximumQuoteGapSeconds=15;
        sealed class Episode {
            public PremarketReference Reference;
            public DateTime FirstBrokerTime,LastBrokerTime,FirstReceivedUtc;
            public decimal FirstVolume;
            public RealtimeQuote Last;
        }
        readonly Dictionary<string,Episode> episodes=new Dictionary<string,Episode>(StringComparer.Ordinal);
        static void Utc(DateTime utc){if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC breakout confirmation time required.");}
        public void Clear(){episodes.Clear();}
        public void Remove(string code){if(code!=null)episodes.Remove(code);}
        static bool Stamp(PremarketReference reference,RealtimeQuote quote,DateTime utc,out DateTime brokerTime) {
            brokerTime=default(DateTime);
            return reference!=null&&quote!=null&&quote.Clock!=null&&quote.Clock.Length==6
                &&DateTime.TryParseExact(reference.TargetDateKorea.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+quote.Clock,
                    "yyyyMMddHHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out brokerTime)
                &&quote.ReceivedUtc.Kind==DateTimeKind.Utc&&quote.ReceivedUtc<=utc
                &&utc-quote.ReceivedUtc<=TimeSpan.FromSeconds(MaximumQuoteGapSeconds)
                &&Math.Abs((MarketTime.ToKorea(utc)-brokerTime).TotalSeconds)<=MaximumQuoteGapSeconds;
        }
        static bool SameQuote(RealtimeQuote left,RealtimeQuote right) {
            return left!=null&&right!=null&&left.Code==right.Code&&left.Clock==right.Clock&&left.MarketCode==right.MarketCode
                &&left.Price==right.Price&&left.CumulativeVolume==right.CumulativeVolume&&left.ReceivedUtc==right.ReceivedUtc
                &&left.SequenceConfirmed==right.SequenceConfirmed&&left.ClockShape==right.ClockShape&&left.ClockRawDigits==right.ClockRawDigits;
        }
        void Start(PremarketReference reference,RealtimeQuote quote,DateTime brokerTime) {
            episodes[reference.Code]=new Episode{Reference=reference,FirstBrokerTime=brokerTime,LastBrokerTime=brokerTime,
                FirstReceivedUtc=quote.ReceivedUtc,FirstVolume=quote.CumulativeVolume,Last=quote.Copy()};
        }
        public void Observe(PremarketReference reference,RealtimeQuote quote,StrategySettings settings,DateTime utc) {
            Utc(utc);DateTime brokerTime;
            if(PremarketResearch.EvaluateLive(reference,quote,settings,utc)==null||!Stamp(reference,quote,utc,out brokerTime)) {
                Remove(reference==null?quote==null?null:quote.Code:reference.Code);return;
            }
            Episode previous;
            if(!episodes.TryGetValue(reference.Code,out previous)||!object.ReferenceEquals(previous.Reference,reference)) {
                Start(reference,quote,brokerTime);return;
            }
            if(quote.ReceivedUtc<previous.Last.ReceivedUtc||brokerTime<previous.LastBrokerTime
                ||quote.CumulativeVolume<previous.Last.CumulativeVolume) {Remove(reference.Code);return;}
            if(quote.ReceivedUtc-previous.Last.ReceivedUtc>TimeSpan.FromSeconds(MaximumQuoteGapSeconds)
                ||brokerTime-previous.LastBrokerTime>TimeSpan.FromSeconds(MaximumQuoteGapSeconds)) {
                Start(reference,quote,brokerTime);return;
            }
            if(quote.CumulativeVolume==previous.Last.CumulativeVolume) {
                // A clock-only callback or replay proves no new traded shares.
                // A changed price with the same trade identity contradicts the
                // observation rather than extending the previous episode.
                if(quote.Price!=previous.Last.Price)Remove(reference.Code);
                return;
            }
            previous.Last=quote.Copy();previous.LastBrokerTime=brokerTime;
        }
        public Candidate Evaluate(PremarketReference reference,RealtimeQuote quote,StrategySettings settings,DateTime utc,out string status) {
            Utc(utc);
            var candidate=PremarketResearch.EvaluateLive(reference,quote,settings,utc,out status);
            if(candidate==null)return null;
            Episode episode;DateTime brokerTime;
            if(!Stamp(reference,quote,utc,out brokerTime)) {status="실시간 체결 간격 초과 / 돌파 유지 재확인";return null;}
            if(!episodes.TryGetValue(reference.Code,out episode)||!object.ReferenceEquals(reference,episode.Reference)) {
                status="돌파 유지 관측 대기 / 새 실시간 체결 필요";return null;
            }
            if(!SameQuote(quote,episode.Last)||brokerTime!=episode.LastBrokerTime) {
                status="최신 실시간 체결 재확인 대기";return null;
            }
            double elapsed=Math.Min((episode.LastBrokerTime-episode.FirstBrokerTime).TotalSeconds,
                (episode.Last.ReceivedUtc-episode.FirstReceivedUtc).TotalSeconds);
            int seconds=(int)Math.Max(0,Math.Floor(elapsed));
            if(seconds<settings.BreakoutConfirmationSeconds||episode.Last.CumulativeVolume<=episode.FirstVolume) {
                status="돌파 유지 확인 "+seconds.ToString(CultureInfo.InvariantCulture)+"/"
                    +settings.BreakoutConfirmationSeconds.ToString(CultureInfo.InvariantCulture)+"초 / 관측 중";return null;
            }
            candidate.Reason+=" / SC 돌파 유지 "+settings.BreakoutConfirmationSeconds.ToString(CultureInfo.InvariantCulture)
                +"초·체결 관측 간격 "+MaximumQuoteGapSeconds.ToString(CultureInfo.InvariantCulture)+"초 이내";
            status="돌파 유지 확인 완료 / 주문 전 계좌 확인 필요";return candidate;
        }
    }
}
