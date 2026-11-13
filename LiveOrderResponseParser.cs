using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    // A layout must come from the installed query definition, never from the
    // numerical appearance of a response. In particular, legacy SO1 is ORC.
    public enum OrderResponseLayout { Unknown,Legacy,Modern }
    public enum OrderResponseDisposition { Unknown,Accepted,Rejected }

    public sealed class OrderResponseEvidence {
        public int RequestId;
        public OrderResponseLayout Layout;
        public OrderResponseDisposition Disposition;
        public string BrokerOrderNumber="";
        // These are candidate identifier slots, not independently accepted orders.
        // Unknown-layout slots must be reconciled using an identified schema.
        public string Slot0OrderNumber="",Slot1OrderNumber="",Slot2OrderNumber="",Slot3OrderNumber="";
        public bool ProviderError,ParseFailed;
        public decimal? Message1Number,Message2Number,Message3Number;
        public string SafeDiagnostic="";
        public bool DefiniteRejected {get{return Disposition==OrderResponseDisposition.Rejected;}}
    }

    public static class LiveOrderResponseParser {
        sealed class Field {
            public string Text="";
            public bool Available;
        }
        static Field ReadField(Func<int,object> read,int index) {
            try {
                var raw=read(index);
                return new Field{Available=true,Text=(Convert.ToString(raw,CultureInfo.InvariantCulture)??"").Trim()};
            }catch{return new Field();}
        }
        static bool NumericIdentifier(string value) {
            return value.Length>0&&value.Length<=32&&value.All(c=>c>='0'&&c<='9');
        }
        static string Identifier(Field value){return value.Available&&NumericIdentifier(value.Text)?value.Text:"";}
        static bool Zero(Field value){return value.Available&&NumericIdentifier(value.Text)&&value.Text.All(c=>c=='0');}
        static bool Positive(Field value){return value.Available&&LiveOrderProtocol.ValidBrokerNumber(value.Text);}
        static bool EmptyOrNumber(Field value){return value.Available&&(value.Text==""||NumericIdentifier(value.Text));}
        static string Shape(Field value) {
            if(!value.Available)return "Unavailable";
            if(value.Text.Length==0)return "Blank";
            if(Zero(value))return "Zero";
            return Positive(value)?"PositiveIdentifier":"Invalid";
        }
        static string CompactNumberText(string value) {
            return new string(value.Where(character=>character!=','&&!char.IsWhiteSpace(character)).ToArray());
        }
        static decimal? RawNumber(Field value) {
            if(!value.Available||value.Text.Length==0)return null;
            decimal number;
            return decimal.TryParse(value.Text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint|NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,out number)?(decimal?)number:null;
        }
        static bool NumericCredentialEcho(Field value,string[] secrets) {
            var number=RawNumber(value);if(!number.HasValue)return false;
            if(secrets==null)return true;
            var compact=CompactNumberText(value.Text);
            foreach(var secret in secrets)
                if(value.Text.IndexOf(secret,StringComparison.Ordinal)>=0||compact.IndexOf(CompactNumberText(secret),StringComparison.Ordinal)>=0)return true;
            // Leading zeros and numeric formatting must not turn a credential
            // echo into a seemingly harmless durable decimal message value.
            foreach(var secret in secrets) {
                decimal sensitiveNumber;
                if(decimal.TryParse(CompactNumberText(secret),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,out sensitiveNumber)&&sensitiveNumber==number.Value)return true;
            }
            return false;
        }
        static decimal? Number(Field value,string[] secrets){return secrets==null||NumericCredentialEcho(value,secrets)?null:RawNumber(value);}
        public static OrderResponseEvidence Read(int requestId,Func<int,object> read,IndiDiagnosticSnapshot diagnostic,
            OrderResponseLayout layout,IEnumerable<string> sensitiveValues) {
            if(requestId<=0)throw new ArgumentOutOfRangeException("requestId");
            if(read==null)throw new ArgumentNullException("read");
            if(!Enum.IsDefined(typeof(OrderResponseLayout),layout))throw new ArgumentOutOfRangeException("layout");
            string[] secrets;
            try {
                secrets=(sensitiveValues??Enumerable.Empty<string>()).Where(value=>!string.IsNullOrWhiteSpace(value))
                    .SelectMany(value=>new[]{value,value.Trim()}).Where(value=>value.Length>0)
                    .Distinct(StringComparer.Ordinal).OrderByDescending(value=>value.Length).ToArray();
            }catch{secrets=null;}
            var result=new OrderResponseEvidence{RequestId=requestId,Layout=layout,
                ProviderError=diagnostic!=null&&diagnostic.ErrorState.HasValue&&diagnostic.ErrorState.Value>0};
            // Copy the complete bounded response while its COM callback buffer
            // is valid. An unavailable optional message never erases a valid ID.
            int count=layout==OrderResponseLayout.Legacy?6:8;
            var fields=Enumerable.Range(0,count).Select(index=>ReadField(read,index)).ToArray();
            result.Slot0OrderNumber=Identifier(fields[0]);
            result.Slot1OrderNumber=Identifier(fields[1]);
            if(layout!=OrderResponseLayout.Legacy) {
                result.Slot2OrderNumber=Identifier(fields[2]);
                result.Slot3OrderNumber=Identifier(fields[3]);
            }
            int firstMessage=layout==OrderResponseLayout.Legacy?3:5;
            // Include observed numeric formatting aliases in the same redaction
            // set, so a comma-separated credential cannot leak as free text
            // after its decimal copy was correctly suppressed.
            if(secrets!=null)secrets=secrets.Concat(fields.Skip(firstMessage).Where(value=>NumericCredentialEcho(value,secrets)).Select(value=>value.Text))
                .Distinct(StringComparer.Ordinal).OrderByDescending(value=>value.Length).ToArray();
            result.Message1Number=Number(fields[firstMessage],secrets);
            result.Message2Number=Number(fields[firstMessage+1],secrets);
            result.Message3Number=Number(fields[firstMessage+2],secrets);

            bool normal=diagnostic!=null&&diagnostic.ErrorState==0;
            if(layout==OrderResponseLayout.Legacy) {
                result.ParseFailed=!EmptyOrNumber(fields[0]);
                if(Positive(fields[0])) {
                    if(normal){result.BrokerOrderNumber=fields[0].Text;result.Disposition=OrderResponseDisposition.Accepted;}
                }else if(Zero(fields[0])||result.ProviderError&&EmptyOrNumber(fields[0]))
                    result.Disposition=OrderResponseDisposition.Rejected;
            }else if(layout==OrderResponseLayout.Modern) {
                var venue=fields.Take(3).ToArray();
                result.ParseFailed=venue.Any(value=>!EmptyOrNumber(value));
                var positive=venue.Where(Positive).ToArray();
                // Equal numerical IDs in two venue slots do not prove that the
                // two routes refer to one order. Routing identities are scoped.
                if(!result.ParseFailed&&positive.Length==1&&normal) {
                    result.BrokerOrderNumber=positive[0].Text;result.Disposition=OrderResponseDisposition.Accepted;
                }else if(!result.ParseFailed&&positive.Length==0&&(venue.All(Zero)||result.ProviderError))
                    result.Disposition=OrderResponseDisposition.Rejected;
            }
            // Unknown layouts are diagnostic evidence only. SO0=0 may coexist
            // with an accepted KRX/NXT number in a modern response.
            string shapes="SO0="+Shape(fields[0])+";SO1="+Shape(fields[1]);
            if(layout!=OrderResponseLayout.Legacy)shapes+=";SO2="+Shape(fields[2])+";SO3="+Shape(fields[3]);
            int messageKind=layout==OrderResponseLayout.Legacy?2:4;
            string rawMessages="SO"+messageKind.ToString(CultureInfo.InvariantCulture)+"="+(fields[messageKind].Available?fields[messageKind].Text:"(unavailable)")+"; "+string.Join(" | ",Enumerable.Range(firstMessage,3)
                .Select(index=>"SO"+index.ToString(CultureInfo.InvariantCulture)+"="+(fields[index].Available?fields[index].Text:"(unavailable)")));
            var safeSource=new IndiDiagnosticSnapshot {
                CommState=diagnostic==null?null:diagnostic.CommState,ErrorState=diagnostic==null?null:diagnostic.ErrorState,
                NativeError=diagnostic==null?null:diagnostic.NativeError,ErrorCode=diagnostic==null?null:diagnostic.ErrorCode,
                // Redact complete values before any truncation. Cutting first
                // could expose part of a password at the truncation boundary.
                Message=(diagnostic==null?"":diagnostic.Message??"")+"; "+rawMessages,
                Unavailable=diagnostic==null?"ProviderDiagnostics":diagnostic.Unavailable
            };
            var prefix="OrderResponse layout="+layout+"; result="+result.Disposition+"; "+shapes+"; ";
            var safe=prefix+(secrets==null?"Indi diagnostics: unavailable (redaction input failure).":IndiDiagnosticFormatter.Format(safeSource,secrets));
            result.SafeDiagnostic=safe.Length<=IndiDiagnosticFormatter.MaximumLength?safe:safe.Substring(0,IndiDiagnosticFormatter.MaximumLength-3)+"...";
            return result;
        }
        public static void RequireKrxRoute(OrderResponseEvidence evidence) {
            if(evidence==null)throw new ArgumentNullException("evidence");
            if(evidence.Layout!=OrderResponseLayout.Modern||evidence.Disposition!=OrderResponseDisposition.Accepted)return;
            bool krx=!evidence.ProviderError&&!evidence.ParseFailed&&LiveOrderProtocol.ValidBrokerNumber(evidence.Slot1OrderNumber)
                &&LiveOrderProtocol.BrokerNumbersEqual(evidence.Slot1OrderNumber,evidence.BrokerOrderNumber)
                &&!LiveOrderProtocol.ValidBrokerNumber(evidence.Slot0OrderNumber)&&!LiveOrderProtocol.ValidBrokerNumber(evidence.Slot2OrderNumber);
            if(krx)return;
            evidence.Disposition=OrderResponseDisposition.Unknown;evidence.BrokerOrderNumber="";
            var safe="KRX 주문 응답 대사 필요; "+(evidence.SafeDiagnostic??"").Replace("result=Accepted","result=Unknown");
            evidence.SafeDiagnostic=safe.Length<=IndiDiagnosticFormatter.MaximumLength?safe:safe.Substring(0,IndiDiagnosticFormatter.MaximumLength-3)+"...";
        }
    }
}
