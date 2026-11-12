using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ShinhanNativeTrader {
    // Implementations read only installed, supported diagnostic getters. Raw provider
    // text stays in memory and must pass through the formatter before reaching a log.
    public interface IIndiDiagnostics {
        IndiDiagnosticSnapshot ReadDiagnostics();
    }

    public sealed class IndiDiagnosticSnapshot {
        public int? CommState, ErrorState, NativeError;
        public string ErrorCode, Message, Unavailable;
    }

    public static class IndiDiagnosticFormatter {
        public const int MaximumLength = 512;
        const string Redacted = "[REDACTED]";
        static readonly Regex LongNumber = new Regex(@"\d{6,}", RegexOptions.CultureInvariant);

        // This entry point is only for the documented public quote fields, never
        // arbitrary holdings/order fields. Preserve their exact shapes to diagnose
        // an empty/invalid symbol, price or time; still redact session credentials.
        public static string FormatQuote(string kind,string symbol,string clock,string price,IEnumerable<string> sensitiveValues) {
            string[] secrets;
            try {
                secrets=(sensitiveValues??Enumerable.Empty<string>()).Where(x=>!string.IsNullOrWhiteSpace(x))
                    .SelectMany(x=>new[]{x,x.Trim()}).Distinct(StringComparer.Ordinal).OrderByDescending(x=>x.Length).ToArray();
            }catch{return "QUOTE fields unavailable (redaction input failure).";}
            if(kind!="QUOTE"&&kind!="SNAPSHOT")return "QUOTE fields unavailable (unsupported response kind).";
            Func<string,string> field=raw=>{
                var value=raw??"";
                foreach(var secret in secrets)value=value.Replace(secret,Redacted);
                var escaped=new StringBuilder();
                foreach(var character in value.Take(40)) {
                    if(char.IsControl(character)||char.GetUnicodeCategory(character)==UnicodeCategory.Format)
                        escaped.Append("\\u"+((int)character).ToString("X4",CultureInfo.InvariantCulture));
                    else if(character=='"'||character=='\\')escaped.Append('\\').Append(character);
                    else escaped.Append(character);
                }
                return "\""+escaped+(value.Length>40?"...":"")+"\" (length="+(raw??"").Length+")";
            };
            return kind=="SNAPSHOT"?"TR_1110_11 fields: price[24]="+field(price)
                :"SC fields: symbol[1]="+field(symbol)+"; clock[2]="+field(clock)+"; price[3]="+field(price);
        }

        public static string Format(IndiDiagnosticSnapshot snapshot, IEnumerable<string> sensitiveValues) {
            if (snapshot == null) return "Indi diagnostics: unavailable.";
            string[] secrets;
            try {
                // Long values go first so overlapping passwords do not partially expose
                // an account value. Also cover provider trimming of an entered value.
                secrets = (sensitiveValues ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .SelectMany(value => new[] { value, value.Trim() })
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(value => value.Length).ToArray();
            } catch {
                // Failure while obtaining redaction inputs must never expose provider text.
                return "Indi diagnostics: unavailable (redaction input failure).";
            }

            var text = "Indi diagnostics: CommState=" + Number(snapshot.CommState)
                + "; ErrorState=" + Number(snapshot.ErrorState)
                + "; Win32Error=" + Number(snapshot.NativeError)
                + "; ErrorCode=" + Value(snapshot.ErrorCode, "(empty)")
                + "; Message=" + Value(snapshot.Message, "(empty)")
                + "; Unavailable=" + Value(snapshot.Unavailable, "(none)");
            foreach (var secret in secrets) text = text.Replace(secret, Redacted);
            text = LongNumber.Replace(text, Redacted);

            // A provider description must occupy one physical log line. Remove control,
            // whitespace and bidi/zero-width formatting characters without losing Korean.
            var line = new StringBuilder(text.Length);
            bool previousSpace = false;
            foreach (var character in text) {
                bool space = char.IsControl(character) || char.IsWhiteSpace(character)
                    || char.GetUnicodeCategory(character) == UnicodeCategory.Format;
                if (space) {
                    if (!previousSpace && line.Length > 0) line.Append(' ');
                } else line.Append(character);
                previousSpace = space;
            }
            var safe = line.ToString().Trim();
            return safe.Length <= MaximumLength ? safe : safe.Substring(0, MaximumLength - 3) + "...";
        }

        static string Number(int? value) {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
        }
        static string Value(string value, string missing) { return string.IsNullOrWhiteSpace(value) ? missing : value; }
    }
}
