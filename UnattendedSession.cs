using System;

namespace ShinhanNativeTrader {
    public enum UnattendedAction { Wait, StartScan, CloseSession }

    // A clock-driven account-session plan only. It does not connect to Indi,
    // transmit orders, infer fills, or restore authorization after a restart.
    // The caller must finish its closing work before acknowledging SessionClosed.
    public sealed class UnattendedSession {
        readonly TimeSpan rescanDelay;
        DateTime? lastObservedUtc, closedThroughKorea;

        public bool Armed { get; private set; }
        public bool Closing { get; private set; }
        public bool ScanRunning { get; private set; }
        public DateTime? SessionDateKorea { get; private set; }
        public DateTime? NextScanUtc { get; private set; }
        public string CloseReason { get; private set; }
        public int RescanMinutes { get; private set; }

        public UnattendedSession(int rescanMinutes = 20) {
            if(rescanMinutes < 1 || rescanMinutes > 240)
                throw new ArgumentOutOfRangeException("rescanMinutes", "Rescan interval must be between 1 and 240 minutes.");
            RescanMinutes=rescanMinutes;
            rescanDelay=TimeSpan.FromMinutes(rescanMinutes);
        }

        static void RequireUtc(DateTime utc) {
            if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC timestamp required.","utc");
        }
        static bool Weekday(DateTime koreaDate) {
            return koreaDate.DayOfWeek!=DayOfWeek.Saturday && koreaDate.DayOfWeek!=DayOfWeek.Sunday;
        }
        static DateTime OpeningUtc(DateTime koreaDate) {
            // 09:00 KST is 00:00 UTC on the same calendar date.
            return DateTime.SpecifyKind(koreaDate.Date,DateTimeKind.Utc);
        }
        DateTime NextOpeningUtc(DateTime utc) {
            var korea=MarketTime.ToKorea(utc);
            var date=korea.Date;
            if(closedThroughKorea.HasValue && date<=closedThroughKorea.Value)
                date=closedThroughKorea.Value.AddDays(1);
            else if(korea.TimeOfDay>=new TimeSpan(15,20,0))
                date=date.AddDays(1);
            while(!Weekday(date))date=date.AddDays(1);
            var opening=OpeningUtc(date);
            return date==korea.Date && Rules.ValidTime(korea) ? utc : opening;
        }

        public void Arm(DateTime utc) {
            RequireUtc(utc);
            if(Armed)return; // Repeated button clicks cannot reset an active session.
            Armed=true;Closing=false;ScanRunning=false;
            SessionDateKorea=null;closedThroughKorea=null;CloseReason=null;
            lastObservedUtc=utc;NextScanUtc=NextOpeningUtc(utc);
        }
        public void Disarm() {
            Armed=false;Closing=false;ScanRunning=false;
            SessionDateKorea=null;NextScanUtc=null;CloseReason=null;
            lastObservedUtc=null;closedThroughKorea=null;
        }
        UnattendedAction Close(string reason) {
            Closing=true;CloseReason=reason;NextScanUtc=null;
            return UnattendedAction.CloseSession;
        }

        // workBusy includes any outstanding request and any existing scan. Closing
        // deliberately takes priority over that flag, including after PC sleep.
        public UnattendedAction Tick(DateTime utc,bool workBusy) {
            RequireUtc(utc);
            if(!Armed || Closing)return UnattendedAction.Wait;
            if(lastObservedUtc.HasValue && utc<lastObservedUtc.Value) {
                if(SessionDateKorea.HasValue)return Close("ClockMovedBackwards");
                return UnattendedAction.Wait;
            }
            lastObservedUtc=utc;
            var korea=MarketTime.ToKorea(utc);
            if(SessionDateKorea.HasValue) {
                if(korea.Date!=SessionDateKorea.Value)return Close("SessionDateChanged");
                if(!Rules.ValidTime(korea))return Close("MarketWindowEnded");
            } else {
                if(!Rules.ValidTime(korea) || (closedThroughKorea.HasValue && korea.Date<=closedThroughKorea.Value)) {
                    NextScanUtc=NextOpeningUtc(utc);return UnattendedAction.Wait;
                }
            }
            if(workBusy || ScanRunning || (NextScanUtc.HasValue && utc<NextScanUtc.Value))return UnattendedAction.Wait;
            if(!SessionDateKorea.HasValue)SessionDateKorea=korea.Date;
            ScanRunning=true;NextScanUtc=null;
            return UnattendedAction.StartScan;
        }

        public void ScanCompleted(DateTime utc) {
            RequireUtc(utc);
            // Duplicate, abandoned, or late callbacks cannot move the next due time
            // or rearm a session after the operator has stopped it.
            if(!Armed || Closing || !ScanRunning || !SessionDateKorea.HasValue)return;
            if(lastObservedUtc.HasValue && utc<lastObservedUtc.Value)return;
            lastObservedUtc=utc;ScanRunning=false;
            NextScanUtc=utc.Add(rescanDelay);
        }

        public void SessionClosed() {
            if(!Armed || !Closing || !SessionDateKorea.HasValue)return;
            if(!closedThroughKorea.HasValue || SessionDateKorea.Value>closedThroughKorea.Value)
                closedThroughKorea=SessionDateKorea.Value;
            SessionDateKorea=null;ScanRunning=false;Closing=false;CloseReason=null;
            // The next Tick computes the next weekday opening from its own time.
            NextScanUtc=null;
        }
    }
}
