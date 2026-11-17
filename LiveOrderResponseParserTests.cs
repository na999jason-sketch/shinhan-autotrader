using System;
using System.Collections.Generic;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class LiveOrderResponseParserTests {
        sealed class OnePassSecrets : IEnumerable<string> {
            public int Enumerations;
            public IEnumerator<string> GetEnumerator() {
                Enumerations++;
                if(Enumerations!=1)throw new InvalidOperationException("redaction must use its materialized copy");
                return new[]{"4321","12345678901"}.AsEnumerable().GetEnumerator();
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator(){return GetEnumerator();}
        }
        static IEnumerable<string> FailingSecrets() {yield return "4321";throw new InvalidOperationException("SECRET");}
        static int checks;
        static void Check(bool value,string label){checks++;if(!value)throw new Exception("LiveOrderResponseParserTests: "+label);}
        static OrderResponseEvidence Read(OrderResponseLayout layout,string[] fields,int? state=0,string message="",string[] secrets=null) {
            return LiveOrderResponseParser.Read(7,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=state,ErrorCode="E123",Message=message},layout,secrets);
        }
        public static int Run() {
            checks=0;
            var legacy=Read(OrderResponseLayout.Legacy,new[]{"0000123","0099999","0","140000","10","0"});
            Check(legacy.Disposition==OrderResponseDisposition.Accepted&&legacy.BrokerOrderNumber=="0000123","verified legacy SO0 is the exchange order identity");
            Check(legacy.Slot1OrderNumber=="0099999"&&legacy.Slot2OrderNumber=="","legacy ORC is retained separately and message slots are not order identifiers");
            Check(legacy.Message1Number==140000&&legacy.Message2Number==10&&legacy.Message3Number==0,"legacy numeric result messages retain their typed values without becoming fills");
            var zeroOrc=Read(OrderResponseLayout.Legacy,new[]{"0","1234567","0","2","0",""});
            Check(zeroOrc.DefiniteRejected&&zeroOrc.BrokerOrderNumber=="","legacy ORC cannot rescue a documented zero exchange order number");
            var legacyError=Read(OrderResponseLayout.Legacy,new[]{"","0","0","부족","0",""},1);
            Check(legacyError.DefiniteRejected&&legacyError.ProviderError,"verified provider error without exchange order identity is a rejection");
            var legacyContradiction=Read(OrderResponseLayout.Legacy,new[]{"123","0","0","","",""},1);
            Check(legacyContradiction.Disposition==OrderResponseDisposition.Unknown,"positive legacy identity with provider error remains uncertain rather than freeing a retry");
            Check(Read(OrderResponseLayout.Legacy,new[]{"","0","0","","",""}).Disposition==OrderResponseDisposition.Unknown,"blank legacy identity alone is not a definite rejection");

            var modern=Read(OrderResponseLayout.Modern,new[]{"0","0000777","0","8000000","0","금액","1","0"});
            Check(modern.Disposition==OrderResponseDisposition.Accepted&&modern.BrokerOrderNumber=="0000777","verified modern zero SOR with positive KRX is not falsely rejected");
            Check(modern.Slot0OrderNumber=="0"&&modern.Slot1OrderNumber=="0000777"&&modern.Slot3OrderNumber=="8000000","modern venue and ORC identifiers are separately copied");
            Check(modern.Message1Number==null&&modern.Message2Number==1,"modern messages use SO5 through SO7 rather than legacy offsets");
            Check(Read(OrderResponseLayout.Modern,new[]{"0","0","444","999","0","","",""}).BrokerOrderNumber=="444","sole modern NXT identity is preserved without a SOR assumption");
            Check(Read(OrderResponseLayout.Modern,new[]{"111","0","0","999","0","","",""}).BrokerOrderNumber=="111","sole modern SOR identity is preserved");
            Check(Read(OrderResponseLayout.Modern,new[]{"111","222","0","999","0","","",""}).Disposition==OrderResponseDisposition.Unknown,"different modern parent and venue identities require routing reconciliation");
            Check(Read(OrderResponseLayout.Modern,new[]{"00111","111","0","999","0","","",""}).Disposition==OrderResponseDisposition.Unknown,"equal numerical identifiers in different venue slots do not prove one routed order");
            Check(Read(OrderResponseLayout.Modern,new[]{"0","0","0","999","0","","",""}).DefiniteRejected,"verified modern all-zero venue identities are rejection evidence even with an ORC identity");
            Check(Read(OrderResponseLayout.Modern,new[]{"0","","","999","0","","",""}).Disposition==OrderResponseDisposition.Unknown,"incomplete modern venue absence cannot establish a zero-only rejection");
            Check(Read(OrderResponseLayout.Modern,new[]{"","","","999","0","","",""},1).DefiniteRejected,"known modern provider rejection with no venue number remains distinct from an acceptance");
            Check(Read(OrderResponseLayout.Modern,new[]{"0","123","0","999","0","","",""},1).Disposition==OrderResponseDisposition.Unknown,"positive modern identity plus provider error is uncertain");
            Check(Read(OrderResponseLayout.Modern,new[]{"0","123","0","999","0","","",""},null).Disposition==OrderResponseDisposition.Unknown,"unknown response error status cannot certify a modern acceptance");
            foreach(var fields in new[]{new[]{"0","123","0","999","0","","",""},new[]{"0","0","0","0","0","","",""},new[]{"123","0","0","0","0","","",""}})
                Check(Read(OrderResponseLayout.Unknown,fields,1).Disposition==OrderResponseDisposition.Unknown,"unknown layout never guesses acceptance or definite rejection from numeric positions");
            var malformed=Read(OrderResponseLayout.Modern,new[]{"0","A123","0","999","0","","",""});
            Check(malformed.ParseFailed&&malformed.Slot1OrderNumber==""&&malformed.Disposition==OrderResponseDisposition.Unknown,"malformed identity is neither retained as a number nor treated as definite rejection");
            Check(Read(OrderResponseLayout.Legacy,new[]{"１２３","0","0","","",""}).ParseFailed,"non-ASCII digits cannot become a broker order identity");
            var unavailable=LiveOrderResponseParser.Read(7,index=>{if(index==1)throw new InvalidOperationException("SECRET1234");return "0";},new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,null);
            Check(unavailable.ParseFailed&&unavailable.Disposition==OrderResponseDisposition.Unknown&&!unavailable.SafeDiagnostic.Contains("SECRET"),"unavailable venue fields preserve uncertainty without copying exception text");
            var optionalUnavailable=LiveOrderResponseParser.Read(7,index=>{if(index>=2)throw new InvalidOperationException("SECRET1234");return index==0?"555":"777";},new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Legacy,null);
            Check(optionalUnavailable.Disposition==OrderResponseDisposition.Accepted&&!optionalUnavailable.ParseFailed,"optional legacy message getter failure does not erase an established primary order identity");

            const string account="12345678901",password="4321";
            var safe=Read(OrderResponseLayout.Modern,new[]{"0","0","0","0","0","계좌 "+account+" 암호 "+password+"\n수량부족","-2","0"},1,
                "오류 "+account+" "+password+"\u202E\r\n"+new string('x',800),new[]{account,password});
            Check(safe.SafeDiagnostic.Length<=512&&!safe.SafeDiagnostic.Contains(account)&&!safe.SafeDiagnostic.Contains(password),"bounded diagnostic redacts session account and password across provider and result messages");
            Check(!safe.SafeDiagnostic.Any(c=>char.IsControl(c)||char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format),"provider diagnostics cannot introduce multiline or bidi control content");
            var boundary=Read(OrderResponseLayout.Legacy,new[]{"0","0","1",new string('x',254)+"ABCDEFGH","",""},1,"",new[]{"ABCDEFGH"});
            Check(!boundary.SafeDiagnostic.Contains("ABCD"),"credential redaction precedes message truncation even at an old field-length boundary");
            Check(!modern.SafeDiagnostic.Contains("8000000")&&!modern.SafeDiagnostic.Contains("0000777"),"diagnostic uses finite identifier shapes rather than persisting venue or ORC identifiers in free text");
            Check(safe.Message2Number==-2,"numeric message evidence is preserved without inferring a position or orderable balance");
            var mutable=new[]{"0","123","0","999","0","first","2","0"};
            var copied=Read(OrderResponseLayout.Modern,mutable);mutable[1]="777";mutable[5]="later";
            Check(copied.BrokerOrderNumber=="123"&&!copied.SafeDiagnostic.Contains("later"),"callback copies identifiers and redacted messages before the native response buffer can change");
            var longId=Read(OrderResponseLayout.Modern,new[]{"0",new string('1',33),"0","999","0","","",""});
            Check(longId.ParseFailed&&longId.Slot1OrderNumber=="","oversized order identifiers cannot enter durable evidence");
            var money=Read(OrderResponseLayout.Legacy,new[]{"123","0","0","1,234.50","not numeric",""});
            Check(money.Message1Number==1234.50m&&!money.Message2Number.HasValue&&!money.Message3Number.HasValue,"optional numeric response values are invariant and never default missing values to zero");
            Check(legacy.SafeDiagnostic.Contains("SO2=0")&&modern.SafeDiagnostic.Contains("SO4=0"),"documented message-kind fields are retained through the redacted diagnostic path for each selected schema");
            var echo=Read(OrderResponseLayout.Legacy,new[]{"4321","0","0","4321","12345678901","42"},0,"4321 12345678901",new[]{"4321","12345678901"});
            Check(!echo.Message1Number.HasValue&&!echo.Message2Number.HasValue&&echo.Message3Number==42,"optional numeric password and account echoes are suppressed instead of becoming durable decimals");
            Check(echo.BrokerOrderNumber=="4321"&&echo.Disposition==OrderResponseDisposition.Accepted,"credential collisions do not alter actual broker identifier evidence");
            Check(!echo.SafeDiagnostic.Contains("4321")&&!echo.SafeDiagnostic.Contains("12345678901"),"numeric echo diagnostics retain credential redaction");
            var formattedEcho=Read(OrderResponseLayout.Modern,new[]{"0","123","0","999","0","4,321","12,345,678,901","004321"},0,"",new[]{"4321","12345678901"});
            Check(!formattedEcho.Message1Number.HasValue&&!formattedEcho.Message2Number.HasValue&&!formattedEcho.Message3Number.HasValue,"formatted and zero-padded numeric credential echoes are not retained as message numbers");
            Check(!formattedEcho.SafeDiagnostic.Contains("4,321")&&!formattedEcho.SafeDiagnostic.Contains("12,345,678,901")&&!formattedEcho.SafeDiagnostic.Contains("004321"),"observed numeric formatting aliases are also removed from free-text diagnostics");
            var containsEcho=Read(OrderResponseLayout.Legacy,new[]{"123","0","0","143210","0","42"},0,"",new[]{"4321"});
            Check(!containsEcho.Message1Number.HasValue&&containsEcho.Message2Number==0,"a larger numeric field containing a credential is suppressed without erasing unrelated values");
            var zeroEcho=Read(OrderResponseLayout.Legacy,new[]{"123","0","0","0","1","42"},0,"",new[]{"0000"});
            Check(!zeroEcho.Message1Number.HasValue&&zeroEcho.Message2Number==1,"numeric conversion cannot strip credential leading zeros into an unredacted decimal");
            var onePass=new OnePassSecrets();
            var once=LiveOrderResponseParser.Read(7,index=>new[]{"0","0","0","0","0","4321","12345678901","42"}[index],new IndiDiagnosticSnapshot{ErrorState=1,Message="4321"},OrderResponseLayout.Modern,onePass);
            Check(onePass.Enumerations==1&&!once.Message1Number.HasValue&&!once.Message2Number.HasValue&&!once.SafeDiagnostic.Contains("4321"),"sensitive values are materialized once for both numeric suppression and diagnostic redaction");
            var failedSecrets=LiveOrderResponseParser.Read(7,index=>new[]{"0","0","0","0","0","4321","12345678901","42"}[index],new IndiDiagnosticSnapshot{ErrorState=1,Message="4321"},OrderResponseLayout.Modern,FailingSecrets());
            Check(!failedSecrets.Message1Number.HasValue&&!failedSecrets.Message2Number.HasValue&&!failedSecrets.Message3Number.HasValue,"redaction-input failure suppresses all optional numeric message evidence");
            Check(failedSecrets.SafeDiagnostic.Contains("redaction input failure")&&!failedSecrets.SafeDiagnostic.Contains("4321")&&!failedSecrets.SafeDiagnostic.Contains("SECRET"),"redaction-input failure cannot fall back to unredacted provider diagnostics");
            var krx=Read(OrderResponseLayout.Modern,new[]{"0","0000777","0","999","0","","",""});
            LiveOrderResponseParser.RequireKrxRoute(krx);
            Check(krx.Disposition==OrderResponseDisposition.Accepted&&krx.BrokerOrderNumber=="0000777","the configured KRX route accepts its sole verified KRX identity");
            foreach(var venue in new[]{new[]{"777","0","0","999","0","","",""},new[]{"0","0","888","999","0","","",""}}) {
                var routed=Read(OrderResponseLayout.Modern,venue);LiveOrderResponseParser.RequireKrxRoute(routed);
                Check(routed.Disposition==OrderResponseDisposition.Unknown&&routed.BrokerOrderNumber=="","SOR-only and NXT-only responses cannot satisfy a configured KRX request");
                Check(routed.Slot0OrderNumber==venue[0]&&routed.Slot2OrderNumber==venue[2],"route mismatch preserves captured venue evidence for reconciliation");
                Check(routed.SafeDiagnostic.Contains("KRX")&&routed.SafeDiagnostic.Contains("result=Unknown")&&!routed.SafeDiagnostic.Contains("result=Accepted")&&routed.SafeDiagnostic.Length<=512,"route mismatch is explicit and bounded in the already-redacted diagnostic");
            }
            LiveOrderResponseParser.RequireKrxRoute(legacy);
            Check(legacy.Disposition==OrderResponseDisposition.Accepted&&legacy.BrokerOrderNumber=="0000123","KRX route enforcement does not reinterpret a verified legacy layout");
            return checks;
        }
    }
}
