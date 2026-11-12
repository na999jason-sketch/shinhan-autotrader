using System;
using System.Collections.Generic;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class IndiDiagnosticTests {
        static int checks;
        static void Check(bool value, string label) {
            checks++;
            if (!value) throw new Exception("IndiDiagnosticTests: " + label);
        }
        static IEnumerable<string> BrokenSecrets() {
            yield return "1234";
            throw new InvalidOperationException("secret enumeration failed");
        }
        public static int Run() {
            checks = 0;
            var snapshot = new IndiDiagnosticSnapshot {
                CommState = 0, ErrorState = 1, ErrorCode = "-101",
                Message = "서버가 연결되지 않았습니다. 로그인 상태를 확인하십시오.",
                Unavailable = "GetErrorCode: MissingMethodException"
            };
            var line = IndiDiagnosticFormatter.Format(snapshot, null);
            Check(line.Contains("CommState=0") && line.Contains("ErrorState=1"), "numeric state values remain useful");
            Check(line.Contains("ErrorCode=-101"), "short provider error code is preserved");
            Check(line.Contains("연결되지 않았습니다") && line.Contains("로그인 상태"), "localized reason is preserved");
            Check(line.Contains("MissingMethodException"), "unavailable getter metadata is preserved");

            snapshot.Message = "account=1234567890, password=x1234y, second=1234";
            line = IndiDiagnosticFormatter.Format(snapshot, new[] { "1234567890", "1234", "", null });
            Check(!line.Contains("1234567890") && !line.Contains("1234"), "account and embedded four digit password are redacted");
            Check(line.Contains("x[REDACTED]y"), "embedded password redaction preserves surrounding reason");
            snapshot.Message = "account=AB-12-3456, password=Secret!";
            line = IndiDiagnosticFormatter.Format(snapshot, new[] { "AB-12-3456", "Secret!" });
            Check(!line.Contains("AB-12-3456") && !line.Contains("Secret!"), "non numeric credentials are redacted");
            snapshot.Message = "trimmed account=AB-12-3456";
            line = IndiDiagnosticFormatter.Format(snapshot, new[] { "  AB-12-3456  " });
            Check(!line.Contains("AB-12-3456"), "provider trimmed credential is covered");

            snapshot.ErrorCode = "987654321";
            snapshot.Message = "unknown digits 765432 and unicode １２３４５６ remain private";
            line = IndiDiagnosticFormatter.Format(snapshot, null);
            Check(!line.Contains("987654321") && !line.Contains("765432") && !line.Contains("１２３４５６"), "long numeric fields are redacted without known secrets");
            snapshot.Message = "a12345678b";
            line = IndiDiagnosticFormatter.Format(snapshot, new[] { "3456", "12345678" });
            Check(line.Contains("a[REDACTED]b"), "overlapping credentials redact longest value first");

            snapshot.ErrorCode = "E01";
            snapshot.Message = "첫 줄\r\n다음 줄\t값\0끝\u202E방향\u200B문자";
            snapshot.Unavailable = "GetCommState\nNotSupportedException";
            line = IndiDiagnosticFormatter.Format(snapshot, null);
            Check(!line.Any(char.IsControl) && !line.Contains("\u202E") && !line.Contains("\u200B"), "control and formatting characters cannot create log lines");
            Check(line.Contains("첫 줄 다음 줄 값 끝 방향 문자"), "control cleanup retains readable Korean reason");
            snapshot.Message = "pa\nss";
            line = IndiDiagnosticFormatter.Format(snapshot, new[] { "pa\nss" });
            Check(line.Contains("Message=[REDACTED]"), "credential redaction occurs before control cleanup");

            snapshot.Message = new string('가', 3000);
            line = IndiDiagnosticFormatter.Format(snapshot, null);
            Check(line.Length == IndiDiagnosticFormatter.MaximumLength && line.EndsWith("...", StringComparison.Ordinal), "diagnostics have a fixed maximum length");
            var originalMessage = snapshot.Message;
            var originalCode = snapshot.ErrorCode;
            IndiDiagnosticFormatter.Format(snapshot, new[] { "가" });
            Check(snapshot.Message == originalMessage && snapshot.ErrorCode == originalCode && snapshot.CommState == 0 && snapshot.ErrorState == 1,
                "formatting never mutates raw snapshot");

            line = IndiDiagnosticFormatter.Format(new IndiDiagnosticSnapshot(), null);
            Check(line.Contains("CommState=unknown") && line.Contains("Message=(empty)") && line.Contains("Unavailable=(none)"), "blank diagnostics are explicit");
            Check(IndiDiagnosticFormatter.Format(null, new[] { "1234" }).Contains("unavailable"), "null snapshot is supported");
            line = IndiDiagnosticFormatter.Format(snapshot, BrokenSecrets());
            Check(line.Contains("redaction input failure") && !line.Contains(originalMessage), "redaction input failure withholds provider text");
            return checks;
        }
    }
}
