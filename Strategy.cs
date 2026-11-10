using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public sealed class Bar {
        public string Date;
        public decimal Open, High, Low, Close, Volume;
    }
    public sealed class Stock {
        public string Code, Name, Market;
    }
    public sealed class Holding {
        public string Code, Name;
        public decimal Quantity, SellPending, BuyPending, Price, AvgPrice, CreditQty;
    }
    public sealed class Candidate {
        public string Code, Name, Reason;
        public decimal Price, Score;
    }
    public sealed class PaperPosition {
        public string Code, Name;
        public int Qty;
        public decimal Entry, High;
    }
    public static class Rules {
        public static decimal Num(object raw) {
            if (raw == null) return 0;
            var s=Convert.ToString(raw, CultureInfo.InvariantCulture).Replace(",", "").Trim();
            decimal n;
            return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0;
        }
        public static string Code(object raw) {
            var value=(Convert.ToString(raw) ?? "").Trim();
            if (value.StartsWith("A") && value.Length == 7) value=value.Substring(1);
            return value.Length==6 && value.All(char.IsDigit) ? value : "";
        }
        public static decimal Avg(IEnumerable<decimal> values) {
            var arr=values.ToArray();return arr.Length==0?0:arr.Average();
        }
        public static Candidate Entry(string code, string name, IEnumerable<Bar> source) {
            // Last daily candle can be an unfinished current session; this is signal research only.
            var b=source.Where(x=>x.Close>0 && x.Volume>0 && x.High>=x.Low)
                .OrderBy(x=>x.Date,StringComparer.Ordinal).GroupBy(x=>x.Date).Select(g=>g.Last()).ToArray();
            if(b.Length<27)return null;
            var last=b[b.Length-1];
            var prior=b.Take(b.Length-1).ToArray();
            var prev5=Avg(prior.Skip(prior.Length-5).Select(x=>x.Close));
            var prev20=Avg(prior.Skip(prior.Length-20).Select(x=>x.Close));
            var prevHigh=prior.Skip(prior.Length-20).Max(x=>x.High);
            var prevVolume=Avg(prior.Skip(prior.Length-20).Select(x=>x.Volume));
            if(prev5<=prev20 || last.Close<=prevHigh || last.Volume < prevVolume*1.3m || last.Close<2000m || last.Close>400000m)return null;
            var score=(last.Close / prevHigh-1m)*100m + (last.Volume/prevVolume - 1m)*3m;
            return new Candidate{Code=code,Name=name,Price=last.Close,Score=decimal.Round(score,2),
                Reason="5일선>20일선 + 직전20봉 고점돌파 + 거래량 1.3배"};
        }
        public static string Exit(PaperPosition p, decimal price) {
            if(p==null || price<=0)return "";
            if(price<=p.Entry*0.94m)return "손절 -6%";
            if(price>=p.Entry*1.12m)return "익절 +12%";
            if(p.High >= p.Entry*1.04m && price<=p.High*0.93m)return "추적손절 -7%";
            return "";
        }
        public static int PaperQty(decimal cash, decimal price, decimal fraction=0.2m) {
            if(cash<=0 || price<=0 || fraction<=0 || fraction>1)return 0;
            return (int)Math.Min(100000, Math.Floor(cash*fraction/(price*1.003m)));
        }
        public static bool ValidTime(DateTime t) {
            return t.DayOfWeek!=DayOfWeek.Sunday && t.DayOfWeek!=DayOfWeek.Saturday && t.TimeOfDay>=new TimeSpan(9,0,0)
                && t.TimeOfDay < new TimeSpan(15,20,0);
        }
    }
}
