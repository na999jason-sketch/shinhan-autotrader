using System;

namespace ShinhanNativeTrader {
    public static class UnattendedSessionTests {
        static int checks;
        static void Check(bool value,string label) {
            checks++;if(!value)throw new Exception("UnattendedSessionTests: "+label);
        }
        static DateTime K(int day,int hour,int minute=0,int second=0) {
            return new DateTime(2026,11,day,hour,minute,second,DateTimeKind.Utc).AddHours(-9);
        }
        static void Reject(Action action,string label) {
            bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}
            Check(rejected,label);
        }
        public static int Run() {
            checks=0;
            var plan=new UnattendedSession();
            Check(!plan.Armed&&plan.Tick(K(13,9),false)==UnattendedAction.Wait,"new process never arms unattended operation");
            Check(plan.RescanMinutes==20,"default rescan delay is twenty minutes");
            Reject(()=>new UnattendedSession(0),"zero rescan interval rejected");
            Reject(()=>new UnattendedSession(241),"unbounded rescan interval rejected");
            Reject(()=>plan.Arm(new DateTime(2026,11,12,20,0,0,DateTimeKind.Local)),"arming requires an explicit UTC timestamp");
            Reject(()=>plan.Tick(new DateTime(2026,11,13,9,0,0),false),"tick never depends on PC local timezone");

            plan.Arm(K(12,20));
            Check(plan.Armed&&!plan.SessionDateKorea.HasValue&&plan.NextScanUtc==K(13,9),"Thursday evening waits for Friday opening");
            Check(plan.Tick(K(12,20),false)==UnattendedAction.Wait,"after-hours arming does not start requests");
            Check(plan.Tick(K(13,8,59,59),false)==UnattendedAction.Wait,"no scan one second before opening");
            Check(plan.Tick(K(13,9),true)==UnattendedAction.Wait&&!plan.SessionDateKorea.HasValue,"opening waits for an existing request or scan");
            Check(plan.Tick(K(13,9),false)==UnattendedAction.StartScan&&plan.ScanRunning&&plan.SessionDateKorea==new DateTime(2026,11,13),"first idle tick at opening starts the correct KST date");
            plan.Arm(K(13,9,1));
            Check(plan.ScanRunning&&plan.SessionDateKorea==new DateTime(2026,11,13),"repeated arming cannot reset an active scan");
            Check(plan.Tick(K(13,9,2),false)==UnattendedAction.Wait,"one consumed decision cannot start duplicate scans");
            plan.ScanCompleted(K(13,9,35));
            Check(!plan.ScanRunning&&plan.NextScanUtc==K(13,9,55),"rescan waits twenty minutes after completion rather than start");
            plan.ScanCompleted(K(13,9,36));
            Check(plan.NextScanUtc==K(13,9,55),"duplicate completion cannot postpone rescan");
            Check(plan.Tick(K(13,9,54,59),false)==UnattendedAction.Wait,"rescan does not run before due time");
            Check(plan.Tick(K(13,9,55),true)==UnattendedAction.Wait,"due rescan still respects outstanding work");
            Check(plan.Tick(K(13,9,56),false)==UnattendedAction.StartScan,"overdue rescan starts exactly once when idle");
            Check(plan.Tick(K(13,15,19,59),true)==UnattendedAction.Wait,"active scan can remain in flight just before close");
            Check(plan.Tick(K(13,15,20),true)==UnattendedAction.CloseSession&&plan.Closing,"closing takes precedence over an in-flight request");
            Check(plan.SessionDateKorea==new DateTime(2026,11,13)&&plan.CloseReason=="MarketWindowEnded","closing retains the date and reason for its report");
            plan.ScanCompleted(K(13,15,21));
            Check(plan.Closing&&!plan.NextScanUtc.HasValue,"late completion cannot schedule a closing session");
            Check(plan.Tick(K(16,9),false)==UnattendedAction.Wait&&plan.Closing,"next Monday cannot begin until close work is acknowledged");
            plan.SessionClosed();
            Check(plan.Armed&&!plan.Closing&&!plan.SessionDateKorea.HasValue,"closing acknowledgement preserves explicit multi-day arming");
            Check(plan.Tick(K(16,9),false)==UnattendedAction.StartScan&&plan.SessionDateKorea==new DateTime(2026,11,16),"Monday begins only after Friday close is acknowledged");

            plan.Disarm();
            plan.ScanCompleted(K(16,9,1));plan.SessionClosed();
            Check(!plan.Armed&&!plan.ScanRunning&&!plan.SessionDateKorea.HasValue&&!plan.NextScanUtc.HasValue,"stop and late callbacks clear all operational state");
            Check(plan.Tick(K(17,9),false)==UnattendedAction.Wait,"operator stop does not reopen on a later weekday");

            plan=new UnattendedSession();plan.Arm(K(13,16));
            Check(plan.NextScanUtc==K(16,9),"Friday after close waits until Monday");
            Check(plan.Tick(K(14,10),false)==UnattendedAction.Wait,"Saturday never starts a scan");
            Check(plan.Tick(K(15,10),false)==UnattendedAction.Wait,"Sunday never starts a scan");
            Check(plan.NextScanUtc==K(16,9),"weekend waiting keeps Monday opening");

            plan=new UnattendedSession();plan.Arm(K(13,10));
            Check(plan.Tick(K(13,10),false)==UnattendedAction.StartScan,"arming during the application window can start immediately");
            Check(plan.Tick(K(16,10),true)==UnattendedAction.CloseSession&&plan.CloseReason=="SessionDateChanged","sleeping across an entire close reports the previous session first");
            Check(plan.SessionDateKorea==new DateTime(2026,11,13),"day rollover does not mislabel Friday's report as Monday");
            plan.SessionClosed();
            Check(plan.Tick(K(16,10),false)==UnattendedAction.StartScan,"awake session can start the new weekday after close acknowledgement");

            plan=new UnattendedSession();plan.Arm(K(13,10));plan.Tick(K(13,10),false);
            Check(plan.Tick(K(13,9,59),false)==UnattendedAction.CloseSession&&plan.CloseReason=="ClockMovedBackwards","backward system clock closes an active session");
            plan.SessionClosed();
            Check(plan.Tick(K(13,10,30),false)==UnattendedAction.Wait&&plan.NextScanUtc==K(16,9),"closed day cannot reopen automatically after clock correction");
            plan.Disarm();plan.Arm(K(13,10,30));
            Check(plan.Tick(K(13,10,30),false)==UnattendedAction.StartScan,"explicit operator stop and rearm can restart the same date");

            plan=new UnattendedSession();plan.Arm(K(12,20));
            Check(plan.Tick(K(12,10),false)==UnattendedAction.Wait&&!plan.SessionDateKorea.HasValue,"clock rollback before arming cannot start an earlier session");
            Check(plan.Tick(K(13,9),false)==UnattendedAction.StartScan,"waiting plan resumes when time catches up");
            Reject(()=>plan.ScanCompleted(new DateTime(2026,11,13,10,0,0)),"completion also requires explicit UTC");
            return checks;
        }
    }
}
