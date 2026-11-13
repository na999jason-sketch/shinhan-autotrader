using System;

namespace ShinhanNativeTrader {
    public static class RealtimeDiagnosticsTests {
        static int checks;
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("RealtimeDiagnosticsTests: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,name);}
        public static int Run() {
            checks=0;
            Check(RealtimeDiagnostics.NormalizeClock("091530")=="091530","HHmmss retains its leading zero");
            Check(RealtimeDiagnostics.NormalizeClock("09:15:30")=="091530","literal colon-separated provider time is normalized without a PC clock substitute");
            Check(RealtimeDiagnostics.NormalizeClock(" 09:15:30 ")=="091530","padding is trimmed without changing the reported time");
            foreach(var invalid in new[]{"",null,"91530","09153000","20261113","24:00:00","23:60:00","236000","09:15:60","09-15-30","SECRET","０９１５３０"})
                Check(RealtimeDiagnostics.NormalizeClock(invalid)=="","unsupported or invalid provider clock cannot establish time");
            Check(RealtimeDiagnostics.ClockShape("09153000")=="digits8"&&RealtimeDiagnostics.ClockShape("09:15:30")=="colon8","shape distinguishes fractional or wider numeric time from colon time");
            Check(RealtimeDiagnostics.ClockShape("SECRET12345678901")=="other","unrecognized raw strings become a finite category");

            var utc=new DateTime(2026,11,13,0,15,30,DateTimeKind.Utc);var diagnostic=new RealtimeDiagnostics();
            Check(diagnostic.StateAt(utc)=="SC 콜백 없음","registration is distinguishable from any callback observation");
            diagnostic.RecordCallback("AA",utc);diagnostic.RecordCallback("AD",utc);diagnostic.RecordCallback("SECRET12345678901",utc);diagnostic.RecordCallback(42,utc);
            Check(diagnostic.CallbackCount==4&&diagnostic.OrderCallbackCount==1&&diagnostic.BalanceCallbackCount==1&&diagnostic.OtherCallbackCount==2,"callbacks count only finite event classes");
            Check(diagnostic.StateAt(utc)=="SC 콜백 없음","account or unknown callbacks do not prove the SC path");
            Check(!diagnostic.Describe(utc).Contains("SECRET")&&!diagnostic.Describe(utc).Contains("12345678901"),"arbitrary event tags never enter diagnostics");
            diagnostic.RecordCallback("SC",utc);Check(diagnostic.StateAt(utc)=="SC 필드 확인 대기","callback receipt and successful quote copying are separate observations");
            var quote=new RealtimeQuote{Code="005930",Clock="091530",ClockShape="colon8",MarketCode="1",Price=70000,CumulativeVolume=100,ReceivedUtc=utc};
            var states=new[]{RealtimeQuoteState.Untracked,RealtimeQuoteState.InvalidClock,RealtimeQuoteState.NonRegularMarket,RealtimeQuoteState.FirstTick,RealtimeQuoteState.Replay,RealtimeQuoteState.Regressed};
            foreach(var state in states){diagnostic.ObserveQuote(quote,state);Check(diagnostic.LastState==state&&diagnostic.StateAt(utc)!="정상","distinct SC rejection reasons remain visible");}
            diagnostic.ObserveQuote(quote,RealtimeQuoteState.Confirmed);
            Check(diagnostic.StateAt(utc)=="정상"&&diagnostic.LastClock=="091530"&&diagnostic.LastClockShape=="colon8","diagnostic uses actual normalized exchange time");
            Check(diagnostic.Describe(utc).Length<=160,"status description fits a compact UI line");
            Check(diagnostic.StateAt(utc.AddSeconds(46))=="SC 수신 지연","previously confirmed callbacks age without new receipts");
            Check(diagnostic.StateAt(utc.AddSeconds(-1))=="수신시각 역전","clock regression does not look live");
            quote.ReceivedUtc=utc.AddMinutes(2);diagnostic.ObserveQuote(quote,RealtimeQuoteState.Confirmed);
            Check(diagnostic.StateAt(utc.AddMinutes(2))=="체결시각과 PC 시각 불일치","fresh receipt cannot conceal a stale exchange clock");
            quote.ReceivedUtc=utc;quote.Code="SECRET12345678901";quote.MarketCode="SECRET1111";quote.Clock="SECRET1111";quote.ClockShape="SECRET1111";
            diagnostic.ObserveQuote(quote,RealtimeQuoteState.InvalidClock);var description=diagnostic.Describe(utc);
            Check(diagnostic.LastCode=="invalid"&&diagnostic.LastMarket=="other"&&diagnostic.LastClockShape=="other","invalid public field shapes are bounded");
            Check(!description.Contains("SECRET")&&!description.Contains("1111")&&!description.Contains("12345678901"),"malformed field values cannot leak into descriptions");
            long callbacks=diagnostic.CallbackCount;diagnostic.Invalidate();
            Check(diagnostic.CallbackCount==callbacks&&diagnostic.QuoteCallbackCount==1&&!diagnostic.LastQuoteUtc.HasValue,"invalidation preserves lifetime callback counts while removing old quote fields");
            Check(diagnostic.StateAt(utc)=="이벤트 재확인 대기"&&!diagnostic.Describe(utc).Contains("최근"),"system invalidation cannot display an old quote as current");
            quote.Code="005930";quote.Clock="091530";quote.ClockShape="digits6";quote.MarketCode="1";
            diagnostic.ObserveQuote(quote,RealtimeQuoteState.FirstTick);Check(diagnostic.StateAt(utc)=="첫 체결·연속 수신 대기","new observed quote replaces the invalidated diagnostic state without certifying a sequence");
            Reject(()=>diagnostic.RecordCallback("SC",DateTime.SpecifyKind(utc,DateTimeKind.Unspecified)),"callback diagnostics require explicit UTC");
            Reject(()=>diagnostic.StateAt(DateTime.SpecifyKind(utc,DateTimeKind.Local)),"diagnostic evaluation requires UTC");
            Reject(()=>diagnostic.ObserveQuote(quote,(RealtimeQuoteState)999),"unknown state labels are refused");
            return checks;
        }
    }
}
