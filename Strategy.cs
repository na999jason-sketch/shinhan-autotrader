using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public sealed class Bar {
        public string Date;
        public decimal Open, High, Low, Close, Volume;
    }
    public sealed class Stock { public string Code, Name, Market; }
    public sealed class Holding {
        public string Code, Name;
        public decimal Quantity, SellPending, BuyPending, Price, AvgPrice, CreditQty;
    }
    public sealed class Candidate {
        public string Code, Name, Reason, SignalDate;
        public decimal Price, Score;
    }
    public sealed class PositionRiskState {
        public string Code, Name;
        public int Qty;
        public decimal Entry, High;
    }
    public sealed class StrategySettings {
        public int FastPeriod = 5, SlowPeriod = 20, BreakoutPeriod = 20, MaxPositions = 3;
        public decimal VolumeMultiplier = 1.3m, MinPrice = 2000m, MaxPrice = 400000m;
        public decimal StopLossPercent = 6m, TakeProfitPercent = 12m;
        public decimal TrailingActivationPercent = 4m, TrailingDrawdownPercent = 7m;
        public decimal AllocationFraction = 0.2m, FeeRate = 0.003m;
        public StrategySettings Copy() { return (StrategySettings)MemberwiseClone(); }
        public void Validate() {
            if(FastPeriod < 1 || SlowPeriod <= FastPeriod || SlowPeriod > 200 || BreakoutPeriod < 1 || BreakoutPeriod > 200)
                throw new ArgumentException("이동평균/돌파 기간은 빠른선 < 느린선, 1~200일이어야 합니다.");
            if(MaxPositions < 1 || MaxPositions > 100 || VolumeMultiplier <= 0 || VolumeMultiplier > 100)
                throw new ArgumentException("최대 보유종목은 1~100, 거래량 배수는 0 초과 100 이하여야 합니다.");
            if(MinPrice <= 0 || MaxPrice < MinPrice || AllocationFraction <= 0 || AllocationFraction > 1 || FeeRate < 0 || FeeRate > 0.1m)
                throw new ArgumentException("가격범위, 배분비율 또는 모의 비용률이 올바르지 않습니다.");
            if(StopLossPercent <= 0 || StopLossPercent >= 100 || TakeProfitPercent <= 0 || TakeProfitPercent > 1000 ||
               TrailingActivationPercent <= 0 || TrailingActivationPercent > 1000 || TrailingDrawdownPercent <= 0 || TrailingDrawdownPercent >= 100)
                throw new ArgumentException("손절/익절/추적손절 비율을 확인하십시오.");
        }
    }
    public static class MarketTime {
        // KRX wall clock is UTC+09:00. Never depend on the PC or CI runner timezone.
        public static DateTime ToKorea(DateTime utc) {
            if(utc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC timestamp required");
            return DateTime.SpecifyKind(utc.AddHours(9), DateTimeKind.Unspecified);
        }
        public static DateTime NowKorea { get { return ToKorea(DateTime.UtcNow); } }
        public static bool CanUseQuote(IndiReply quote,DateTime receivedUtc) {
            return quote!=null&&quote.HasTradeClock&&quote.Price>0&&!string.IsNullOrEmpty(quote.Code)&&Rules.Code(quote.Code)==quote.Code
                &&IsFreshQuote(quote.Clock,receivedUtc);
        }
        public static bool IsFreshQuote(string hhmmss, DateTime receivedUtc, int maxAgeSeconds = 45) {
            var korea = ToKorea(receivedUtc);
            if(!Rules.ValidTime(korea) || maxAgeSeconds < 0 || string.IsNullOrWhiteSpace(hhmmss)) return false;
            var text = hhmmss.Trim();
            if(text.Length != 6 || text.Any(c => c < '0' || c > '9')) return false;
            DateTime stamp;
            if(!DateTime.TryParseExact(korea.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + text, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp) || !Rules.ValidTime(stamp)) return false;
            // HHMMSS has no trading date. This rejects delayed intraday quotes, but cannot prove
            // the broker's session date; Windows validation must establish that separately.
            return Math.Abs((korea - stamp).TotalSeconds) <= maxAgeSeconds;
        }
    }
    public static class Rules {
        public static decimal Num(object raw) {
            decimal n;
            return TryNum(raw, out n) ? n : 0;
        }
        public static bool TryNum(object raw, out decimal value) {
            value = 0;
            if(raw == null) return false;
            var s = Convert.ToString(raw, CultureInfo.InvariantCulture).Replace(",", "").Trim();
            return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value);
        }
        public static string Code(object raw) {
            var value = (Convert.ToString(raw) ?? "").Trim();
            if(value.StartsWith("A", StringComparison.Ordinal) && value.Length == 7) value = value.Substring(1);
            return value.Length == 6 && value.All(c => c >= '0' && c <= '9') ? value : "";
        }
        public static decimal Avg(IEnumerable<decimal> values) {
            var arr = values.ToArray(); return arr.Length == 0 ? 0 : arr.Average();
        }
        public static Candidate Entry(string code, string name, IEnumerable<Bar> source) {
            return Entry(code, name, source, new StrategySettings());
        }
        public static Candidate Entry(string code, string name, IEnumerable<Bar> source, StrategySettings settings) {
            settings.Validate();
            if(Code(code) == "" || source == null) return null;
            // The supplied last bar may be today's unfinished bar. Entry is a research signal;
            // an actual order also requires a fresh quote and broker capacity verification.
            var b = source.Where(x => x != null && ValidBar(x)).OrderBy(x => x.Date, StringComparer.Ordinal)
                .GroupBy(x => x.Date).Select(g => g.Last()).ToArray();
            var required = Math.Max(settings.SlowPeriod, settings.BreakoutPeriod) + 1;
            if(b.Length < required) return null;
            var last = b[b.Length - 1];
            var prior = b.Take(b.Length - 1).ToArray();
            var fast = Avg(prior.Skip(prior.Length - settings.FastPeriod).Select(x => x.Close));
            var slow = Avg(prior.Skip(prior.Length - settings.SlowPeriod).Select(x => x.Close));
            var high = prior.Skip(prior.Length - settings.BreakoutPeriod).Max(x => x.High);
            var volume = Avg(prior.Skip(prior.Length - settings.BreakoutPeriod).Select(x => x.Volume));
            if(fast <= slow || last.Close <= high || volume <= 0 || last.Volume < volume * settings.VolumeMultiplier ||
                last.Close < settings.MinPrice || last.Close > settings.MaxPrice) return null;
            var score = (last.Close / high - 1m) * 100m + (last.Volume / volume - 1m) * 3m;
            return new Candidate { Code = Code(code), Name = name, Price = last.Close, SignalDate = last.Date, Score = decimal.Round(score, 2),
                Reason = settings.FastPeriod + "일선>" + settings.SlowPeriod + "일선 + 직전" + settings.BreakoutPeriod + "봉 고점돌파 + 거래량 " + settings.VolumeMultiplier.ToString(CultureInfo.InvariantCulture) + "배" };
        }
        static bool ValidBar(Bar b) {
            DateTime date;
            return DateTime.TryParseExact(b.Date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
                b.Open > 0 && b.Close > 0 && b.Volume > 0 && b.Low > 0 && b.High >= Math.Max(b.Open,b.Close) && b.Low <= Math.Min(b.Open,b.Close);
        }
        public static string Exit(PositionRiskState p, decimal price) { return Exit(p, price, new StrategySettings()); }
        public static string Exit(PositionRiskState p, decimal price, StrategySettings settings) {
            settings.Validate();
            if(p == null || p.Entry <= 0 || price <= 0) return "";
            if(price <= p.Entry * (1 - settings.StopLossPercent / 100)) return "손절 -" + settings.StopLossPercent + "%";
            if(price >= p.Entry * (1 + settings.TakeProfitPercent / 100)) return "익절 +" + settings.TakeProfitPercent + "%";
            if(p.High >= p.Entry * (1 + settings.TrailingActivationPercent / 100) && price <= p.High * (1 - settings.TrailingDrawdownPercent / 100))
                return "추적손절 -" + settings.TrailingDrawdownPercent + "%";
            return "";
        }
        public static int OrderQuantity(decimal cash, decimal price, decimal fraction = 0.2m) {
            return OrderQuantity(cash, price, fraction, 0.003m);
        }
        public static int OrderQuantity(decimal cash, decimal price, decimal fraction, decimal feeRate) {
            if(cash <= 0 || price <= 0 || fraction <= 0 || fraction > 1 || feeRate < 0 || feeRate > 0.1m) return 0;
            return (int)Math.Min(100000, Math.Floor(cash * fraction / (price * (1 + feeRate))));
        }
        public static bool ValidTime(DateTime koreaTime) {
            // Weekday/time guard only. It is not a Korean exchange holiday calendar.
            return koreaTime.DayOfWeek != DayOfWeek.Sunday && koreaTime.DayOfWeek != DayOfWeek.Saturday &&
                koreaTime.TimeOfDay >= new TimeSpan(9,0,0) && koreaTime.TimeOfDay < new TimeSpan(15,20,0);
        }
    }
}
