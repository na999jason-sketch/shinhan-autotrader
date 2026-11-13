using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    // Historical facts for one intended trading day. This is a watch reference,
    // never an order, an authorization, or a historical entry dated as today.
    public sealed class PremarketReference {
        readonly StrategySettings settings;
        public string Code {get;private set;}
        public string Name {get;private set;}
        public string Market {get;private set;}
        public DateTime TargetDateKorea {get;private set;}
        public string LastCompletedBarDate {get;private set;}
        public decimal PriorHigh {get;private set;}
        public decimal AverageVolume {get;private set;}
        public decimal FastAverage {get;private set;}
        public decimal SlowAverage {get;private set;}
        public decimal ReferenceClose {get;private set;}
        public decimal RankScore {get;private set;}
        public int CompletedBarCount {get;private set;}
        internal PremarketReference(Stock stock,DateTime target,string lastDate,decimal high,decimal volume,
            decimal fast,decimal slow,decimal close,int count,decimal score,StrategySettings strategy) {
            Code=Rules.Code(stock.Code);Name=stock.Name??Code;Market=stock.Market;TargetDateKorea=target;
            LastCompletedBarDate=lastDate;PriorHigh=high;AverageVolume=volume;FastAverage=fast;SlowAverage=slow;
            ReferenceClose=close;CompletedBarCount=count;RankScore=score;settings=strategy.Copy();
        }
        internal bool Matches(StrategySettings other) {
            return other!=null&&settings.FastPeriod==other.FastPeriod&&settings.SlowPeriod==other.SlowPeriod
                &&settings.BreakoutPeriod==other.BreakoutPeriod&&settings.VolumeMultiplier==other.VolumeMultiplier
                &&settings.MinPrice==other.MinPrice&&settings.MaxPrice==other.MaxPrice;
        }
        internal bool SameEntrySettings(PremarketReference other){return other!=null&&Matches(other.settings);}
    }
    public static class PremarketResearch {
        public const int MaximumWatchlist=200,MaximumCompletedBarAgeDays=10;
        static void TargetDate(DateTime target) {
            if(target.Kind!=DateTimeKind.Unspecified||target.TimeOfDay!=TimeSpan.Zero
                ||target.DayOfWeek==DayOfWeek.Saturday||target.DayOfWeek==DayOfWeek.Sunday)
                throw new ArgumentException("A weekday trading date in Korea, with unspecified kind and no time, is required.");
        }
        static bool Date(string text,out DateTime date) {
            return DateTime.TryParseExact(text,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date);
        }
        static bool ValidCompletedBar(Bar bar) {
            return bar.Open>0&&bar.Close>0&&bar.Low>0&&bar.High>=Math.Max(bar.Open,bar.Close)
                &&bar.Low<=Math.Min(bar.Open,bar.Close)&&bar.Volume>=0&&bar.Volume==decimal.Truncate(bar.Volume);
        }
        public static PremarketReference Create(Stock stock,IEnumerable<Bar> source,StrategySettings settings,DateTime targetTradingDateKorea) {
            if(settings==null)throw new ArgumentNullException("settings");
            var strategy=settings.Copy();strategy.Validate();TargetDate(targetTradingDateKorea);
            if(stock==null||Rules.Code(stock.Code)==""||(stock.Market!="0"&&stock.Market!="1")||source==null)return null;
            var complete=new List<Bar>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var bar in source) {
                DateTime date;
                if(bar==null||!Date(bar.Date,out date)||!seen.Add(bar.Date))return null;
                // Today's unfinished bar, placeholders and later bars cannot
                // enter an average, breakout threshold, or completed count.
                if(date>=targetTradingDateKorea)continue;
                if(!ValidCompletedBar(bar))return null;
                complete.Add(new Bar{Date=bar.Date,Open=bar.Open,High=bar.High,Low=bar.Low,Close=bar.Close,Volume=bar.Volume});
            }
            int required=Math.Max(strategy.SlowPeriod,strategy.BreakoutPeriod);
            if(complete.Count<required)return null;
            var bars=complete.OrderBy(bar=>bar.Date,StringComparer.Ordinal).ToArray();
            var last=bars[bars.Length-1];DateTime lastDate;Date(last.Date,out lastDate);
            if((targetTradingDateKorea-lastDate).TotalDays>MaximumCompletedBarAgeDays)return null;
            try {
                decimal fast=bars.Skip(bars.Length-strategy.FastPeriod).Average(bar=>bar.Close);
                decimal slow=bars.Skip(bars.Length-strategy.SlowPeriod).Average(bar=>bar.Close);
                var breakout=bars.Skip(bars.Length-strategy.BreakoutPeriod).ToArray();
                decimal high=breakout.Max(bar=>bar.High),volume=breakout.Average(bar=>bar.Volume);
                // Preparation includes every valid uptrend in the configured
                // price range. A breakout and volume surge are checked live.
                if(fast<=slow||last.Close<strategy.MinPrice||last.Close>strategy.MaxPrice||volume<=0)return null;
                decimal score=decimal.Round((fast/slow-1m)*100m-(high/last.Close-1m)*100m,6);
                return new PremarketReference(stock,targetTradingDateKorea,last.Date,high,volume,fast,slow,last.Close,bars.Length,score,strategy);
            }catch(OverflowException){return null;}
        }
        public static IList<PremarketReference> Rank(IEnumerable<PremarketReference> references,int capacity=MaximumWatchlist) {
            if(references==null)throw new ArgumentNullException("references");
            if(capacity<0||capacity>MaximumWatchlist)throw new ArgumentOutOfRangeException("capacity");
            var values=references.Where(value=>value!=null).ToList();
            if(values.Count==0)return values.AsReadOnly();
            if(values.Any(value=>value.TargetDateKorea!=values[0].TargetDateKorea||!value.SameEntrySettings(values[0])))
                throw new ArgumentException("Research watchlists must use one target day and one entry strategy.");
            if(values.GroupBy(value=>value.Code,StringComparer.Ordinal).Any(group=>group.Count()>1))
                throw new ArgumentException("A research watchlist cannot contain duplicate stock symbols.");
            return values.OrderByDescending(value=>value.RankScore).ThenBy(value=>value.Code,StringComparer.Ordinal).Take(capacity).ToList().AsReadOnly();
        }
        public static Candidate EvaluateLive(PremarketReference reference,RealtimeQuote quote,StrategySettings settings,DateTime utcNow) {
            if(utcNow.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC evaluation time is required.");
            if(settings==null)throw new ArgumentNullException("settings");
            var strategy=settings.Copy();strategy.Validate();
            if(reference==null||quote==null||!reference.Matches(strategy))return null;
            var korea=MarketTime.ToKorea(utcNow);
            if(reference.TargetDateKorea!=korea.Date||!Rules.ValidTime(korea)||quote.Code!=reference.Code
                ||quote.ReceivedUtc.Kind!=DateTimeKind.Utc||quote.ReceivedUtc>utcNow||utcNow-quote.ReceivedUtc>TimeSpan.FromSeconds(45)
                ||MarketTime.ToKorea(quote.ReceivedUtc).Date!=reference.TargetDateKorea
                ||!quote.SequenceConfirmed||quote.MarketCode!="1"
                ||!MarketTime.CanUseQuote(quote.ToReply(),quote.ReceivedUtc)||!MarketTime.CanUseQuote(quote.ToReply(),utcNow)
                ||quote.Price<strategy.MinPrice||quote.Price>strategy.MaxPrice||quote.Price<=reference.PriorHigh
                ||quote.CumulativeVolume<0||quote.CumulativeVolume!=decimal.Truncate(quote.CumulativeVolume))return null;
            try {
                if(quote.CumulativeVolume<reference.AverageVolume*strategy.VolumeMultiplier)return null;
                decimal ratio=quote.CumulativeVolume/reference.AverageVolume;
                decimal score=decimal.Round((quote.Price/reference.PriorHigh-1m)*100m+(ratio-1m)*3m,2);
                return new Candidate{Code=reference.Code,Name=reference.Name,Price=quote.Price,Score=score,
                    SignalDate=korea.ToString("yyyyMMdd",CultureInfo.InvariantCulture),
                    Reason="완료봉 기준 "+reference.LastCompletedBarDate+" / "+strategy.FastPeriod+"일선>"+strategy.SlowPeriod
                        +"일선 + 직전"+strategy.BreakoutPeriod+"봉 고점 "+reference.PriorHigh.ToString("0.####",CultureInfo.InvariantCulture)
                        +" 돌파 + 당일 누적거래량 "+ratio.ToString("0.##",CultureInfo.InvariantCulture)+"배"};
            }catch(OverflowException){return null;}
        }
    }
}
