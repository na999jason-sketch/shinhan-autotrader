using System;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public enum RealtimeQuoteState { Untracked,InvalidClock,NonRegularMarket,FirstTick,Replay,Regressed,Confirmed }

    // Only finite event categories and public quote fields enter these diagnostics.
    // Account event buffers and arbitrary provider strings must never be copied here.
    public sealed class RealtimeDiagnostics {
        bool invalidated;
        public long CallbackCount {get;private set;}
        public long QuoteCallbackCount {get;private set;}
        public long OrderCallbackCount {get;private set;}
        public long BalanceCallbackCount {get;private set;}
        public long OtherCallbackCount {get;private set;}
        public DateTime? LastCallbackUtc {get;private set;}
        public DateTime? LastQuoteUtc {get;private set;}
        public string LastCode {get;private set;}
        public string LastClock {get;private set;}
        public string LastClockShape {get;private set;}
        public string LastMarket {get;private set;}
        public decimal LastVolume {get;private set;}
        public RealtimeQuoteState LastState {get;private set;}

        static long Next(long value){return value==long.MaxValue?value:value+1;}
        static void Utc(DateTime value){if(value.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC diagnostic timestamp required.");}
        static bool Digits(string value){return value.All(c=>c>='0'&&c<='9');}
        public static string ClockShape(string raw) {
            var value=(raw??"").Trim();
            if(value.Length==0)return "blank";
            if(value.Length==6&&Digits(value))return "digits6";
            if(value.Length==8&&value[2]==':'&&value[5]==':'&&Digits(value.Substring(0,2)+value.Substring(3,2)+value.Substring(6,2)))return "colon8";
            if(value.Length==8&&Digits(value))return "digits8";
            return "other";
        }
        public static string NormalizeClock(string raw) {
            var value=(raw??"").Trim();var shape=ClockShape(value);
            if(shape!="digits6"&&shape!="colon8")return "";
            DateTime parsed;
            if(!DateTime.TryParseExact(value,shape=="digits6"?"HHmmss":"HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))return "";
            return parsed.ToString("HHmmss",CultureInfo.InvariantCulture);
        }
        public void RecordCallback(object kind,DateTime utc) {
            Utc(utc);CallbackCount=Next(CallbackCount);LastCallbackUtc=utc;
            var type=kind as string;
            if(type=="SC")QuoteCallbackCount=Next(QuoteCallbackCount);
            else if(type=="AA")OrderCallbackCount=Next(OrderCallbackCount);
            else if(type=="AD")BalanceCallbackCount=Next(BalanceCallbackCount);
            else OtherCallbackCount=Next(OtherCallbackCount);
        }
        public void ObserveQuote(RealtimeQuote quote,RealtimeQuoteState state) {
            if(quote==null)throw new ArgumentNullException("quote");Utc(quote.ReceivedUtc);
            if(!Enum.IsDefined(typeof(RealtimeQuoteState),state))throw new ArgumentException("Known realtime quote diagnostic state required.");
            LastQuoteUtc=quote.ReceivedUtc;LastCode=!string.IsNullOrEmpty(quote.Code)&&Rules.Code(quote.Code)==quote.Code?quote.Code:"invalid";
            LastClock=NormalizeClock(quote.Clock);LastClockShape=new[]{"blank","digits6","colon8","digits8","other"}.Contains(quote.ClockShape)?quote.ClockShape:"other";
            var market=(quote.MarketCode??"").Trim();LastMarket=market.Length==0?"blank":market.Length==1&&market[0]>='0'&&market[0]<='9'?market:"other";
            LastVolume=quote.CumulativeVolume>=0&&quote.CumulativeVolume==decimal.Truncate(quote.CumulativeVolume)?quote.CumulativeVolume:0;
            LastState=state;invalidated=false;
        }
        public void Invalidate() {
            LastQuoteUtc=null;LastCode=null;LastClock=null;LastClockShape=null;LastMarket=null;LastVolume=0;
            invalidated=true;
        }
        public string StateAt(DateTime utc) {
            Utc(utc);
            if(invalidated)return "이벤트 재확인 대기";
            if(QuoteCallbackCount==0)return "SC 콜백 없음";
            if(!LastQuoteUtc.HasValue)return "SC 필드 확인 대기";
            switch(LastState) {
                case RealtimeQuoteState.Untracked:return "미등록 종목";
                case RealtimeQuoteState.InvalidClock:return "시각 형식 미인식";
                case RealtimeQuoteState.NonRegularMarket:return "정규장 구분 미확인";
                case RealtimeQuoteState.FirstTick:return "첫 체결·연속 수신 대기";
                case RealtimeQuoteState.Replay:return "연속 체결 진행 대기";
                case RealtimeQuoteState.Regressed:return "체결시각·거래량 역행";
            }
            if(LastQuoteUtc.Value>utc)return "수신시각 역전";
            if(utc-LastQuoteUtc.Value>TimeSpan.FromSeconds(45))return "SC 수신 지연";
            if(!Rules.ValidTime(MarketTime.ToKorea(utc)))return "장전·장외 대기";
            if(!MarketTime.IsFreshQuote(LastClock,utc))return "체결시각과 PC 시각 불일치";
            return "정상";
        }
        public string Describe(DateTime utc) {
            var state=StateAt(utc);
            var counts="콜백 SC "+QuoteCallbackCount.ToString(CultureInfo.InvariantCulture)+" / AA "+OrderCallbackCount.ToString(CultureInfo.InvariantCulture)
                +" / AD "+BalanceCallbackCount.ToString(CultureInfo.InvariantCulture)+" / 기타 "+OtherCallbackCount.ToString(CultureInfo.InvariantCulture);
            if(!LastQuoteUtc.HasValue)return state+" · "+counts;
            string clock=LastClock.Length==6?LastClock.Substring(0,2)+":"+LastClock.Substring(2,2)+":"+LastClock.Substring(4,2):"미인식";
            var seconds=(utc-LastQuoteUtc.Value).TotalSeconds;string age=seconds<0?"미래":seconds>9999?"9999+초":Math.Floor(seconds).ToString(CultureInfo.InvariantCulture)+"초";
            return state+" · "+counts+" · 최근 "+LastCode+" "+clock+" ("+LastClockShape+") 장"+LastMarket+" · "+age;
        }
    }
}
