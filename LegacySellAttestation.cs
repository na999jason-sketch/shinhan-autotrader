using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShinhanNativeTrader {
    // Compatibility scope for the operator's prior confirmation. This is an
    // account-bound disposition, never a claim of broker response evidence.
    // Neither the original account number nor its application key is stored.
    public static class LegacySellAttestation {
        const string Domain="ShinhanNativeTrader/legacy-sell-confirmation/20261113/v1/";
        const string ExpectedDigest="550ae763ef9d178a9bbc1fb7298acfa0071355dc05c03246802da118ae12faf7";
        public static bool MatchesAccountKey(string key) {
            if(key==null||key.Length!=64||key.Any(value=>!(value>='0'&&value<='9'||value>='a'&&value<='f')))return false;
            using(var hash=SHA256.Create()) {
                var actual=string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(Domain+key)).Select(value=>value.ToString("x2",CultureInfo.InvariantCulture)));
                return string.Equals(actual,ExpectedDigest,StringComparison.Ordinal);
            }
        }
    }
}
