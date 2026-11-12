using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShinhanNativeTrader {
    public static class IndiTests {
        sealed class FakeIndi : IIndiTransport {
            public event Action<int> ReceiveData;
            public event Action<int> ReceiveSysMsg;
            public event Action<string> Error;
            public int Id=7,Rows,RequestCount;
            public bool SetQueryAccepted=true,SetFieldAccepted=true;
            public string ThrowMethod;
            public Action DuringRequest;
            public readonly Dictionary<string,object> Fields=new Dictionary<string,object>();
            public object Invoke(string method,params object[] args) {
                if(method==ThrowMethod)throw new InvalidOperationException("account-123-password-SECRET");
                switch(method) {
                    case "SetQueryName":return SetQueryAccepted;
                    case "SetSingleData":return SetFieldAccepted;
                    case "RequestData":RequestCount++;if(DuringRequest!=null)DuringRequest();return Id;
                    case "GetMultiRowCount":return Rows;
                    case "GetSingleData":return Field("s:"+args[0]);
                    case "GetMultiData":return Field("m:"+args[0]+":"+args[1]);
                    default:throw new InvalidOperationException("Unexpected fake call "+method);
                }
            }
            object Field(string key){object result;return Fields.TryGetValue(key,out result)?result:"";}
            public void Data(int id){if(ReceiveData!=null)ReceiveData(id);}
            public void Fail(string reason){if(Error!=null)Error(reason);}
            public void Sys(int id){if(ReceiveSysMsg!=null)ReceiveSysMsg(id);}
            public void Dispose(){}
            public void Quote(string code="005930",string price="70,000",string clock="091530") {
                Fields["s:1"]=code;Fields["s:3"]=price;Fields["s:2"]=clock;
            }
            public void Stock() {
                Rows=1;Fields["m:0:1"]="005930";Fields["m:0:2"]="0";Fields["m:0:3"]="Samsung";
                Fields["m:0:14"]="ST";Fields["m:0:6"]="0";Fields["m:0:7"]="0";Fields["m:0:8"]="0";
            }
            public void Bar() {
                Rows=1;Fields["m:0:0"]="20261111";Fields["m:0:2"]="69000";Fields["m:0:3"]="71000";
                Fields["m:0:4"]="68000";Fields["m:0:5"]="70000";Fields["m:0:9"]="123456";
            }
            public void Holding() {
                Rows=1;Fields["m:0:0"]="A005930";Fields["m:0:1"]="Samsung";
                for(int i=2;i<=7;i++)Fields["m:0:"+i]="0";
                Fields["m:0:2"]="10";Fields["m:0:5"]="70000";Fields["m:0:6"]="65000";
            }
        }
        static int checks;
        static void Check(bool condition,string name){checks++;if(!condition)throw new Exception("Indi test failed: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,name);}
        static bool BeginQuote(IndiRequestCoordinator requests){return requests.Begin("QUOTE","SC",new Dictionary<int,string>{{0,"005930"}},"005930");}
        public static int Run() {
            checks=0;
            var fake=new FakeIndi();fake.Quote();var requests=new IndiRequestCoordinator(fake);
            int received=0,fatal=0;IndiReply reply=null;
            requests.Done+=value=>{received++;reply=value;};requests.Fatal+=reason=>fatal++;
            Check(BeginQuote(requests)&&requests.Busy,"asynchronous request stays pending");
            Check(!BeginQuote(requests)&&fake.RequestCount==1,"one request at a time");
            fake.Data(99);Check(received==0&&requests.Busy,"mismatched response ignored");
            fake.Data(7);Check(received==1&&!requests.Busy&&reply.Price==70000,"matching quote copied and completed");
            fake.Data(7);Check(received==1,"duplicate response ignored");
            requests.Timeout();Check(fatal==0,"idle timeout has no effect");
            requests.Dispose();fake.Data(7);Check(received==1,"disposed coordinator unhooks receive handler");

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>{received++;reply=value;};
            fake.DuringRequest=()=>{fake.Data(7);fake.Fields["s:3"]="broken after callback";};
            Check(BeginQuote(requests)&&received==1&&!requests.Busy&&reply.Price==70000,"reentrant receive snapshots buffer before RequestData returns");
            requests.Dispose();

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>received++;
            fake.DuringRequest=()=>{fake.Fields["s:3"]="invalid unrelated response";fake.Data(99);fake.Quote();fake.Data(7);};
            Check(BeginQuote(requests)&&received==1&&!requests.Failed,"early mismatched parse failure cannot poison matching response");
            requests.Dispose();

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);fatal=0;
            requests.Fatal+=reason=>fatal++;BeginQuote(requests);requests.Timeout();fake.Data(7);
            Check(requests.Failed&&!requests.Busy&&fatal==1,"timeout halts and ignores late callback");
            Reject(()=>BeginQuote(requests),"cannot request after timeout");requests.Dispose();

            fake=new FakeIndi{Id=-1};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed,"negative request id halts");requests.Dispose();
            fake=new FakeIndi{SetQueryAccepted=false};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed&&fake.RequestCount==0,"SetQueryName failure prevents request");requests.Dispose();
            fake=new FakeIndi{SetFieldAccepted=false};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed&&fake.RequestCount==0,"SetSingleData failure prevents request");requests.Dispose();

            fake=new FakeIndi{ThrowMethod="SetSingleData"};requests=new IndiRequestCoordinator(fake);
            var diagnostics="";requests.Trace+=value=>diagnostics+=value;requests.Fatal+=value=>diagnostics+=value;
            Check(!BeginQuote(requests)&&requests.Failed,"provider exception during field setter halts");
            Check(diagnostics.Contains("SetSingleData")&&!diagnostics.Contains("SECRET")&&!diagnostics.Contains("account-123"),"credential values inside provider exceptions never reach diagnostics");
            requests.Dispose();
            fake=new FakeIndi{ThrowMethod="GetSingleData"};requests=new IndiRequestCoordinator(fake);diagnostics="";
            requests.Trace+=value=>diagnostics+=value;requests.Fatal+=value=>diagnostics+=value;
            BeginQuote(requests);fake.Data(7);
            Check(requests.Failed&&!diagnostics.Contains("SECRET"),"provider parse exception text omitted");requests.Dispose();

            fake=new FakeIndi();fake.Quote(price:"N/A");requests=new IndiRequestCoordinator(fake);
            BeginQuote(requests);fake.Data(7);Check(requests.Failed,"malformed quote faults closed");requests.Dispose();
            fake=new FakeIndi();fake.Quote(code:"000660");
            Reject(()=>IndiResponseParser.Read(fake,"QUOTE","005930"),"response symbol must match requested symbol");
            fake.Quote(clock:"256100");Reject(()=>IndiResponseParser.Read(fake,"QUOTE","005930"),"invalid quote clock rejected");
            fake.Quote(price:"-70,000");Check(IndiResponseParser.Read(fake,"QUOTE","005930").Price==70000,"signed broker quote price normalized");
            Reject(()=>IndiResponseParser.RequiredNumber("", "cash"),"blank numeric field not zero");
            Reject(()=>IndiResponseParser.RequiredNumber("1e6", "cash"),"scientific notation rejected");
            Reject(()=>IndiResponseParser.RequiredNumber("1,,000", "cash"),"bad comma grouping rejected");
            Reject(()=>IndiResponseParser.RequiredNumber("-1", "cash"),"negative cash rejected");
            Check(IndiResponseParser.RequiredNumber("1,234.50","cash")==1234.5m,"invariant number parsed");
            var priorCulture=CultureInfo.CurrentCulture;
            try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(IndiResponseParser.RequiredNumber("1234.5","cash")==1234.5m,"decimal parser independent of UI culture");}
            finally{CultureInfo.CurrentCulture=priorCulture;}

            fake=new FakeIndi();fake.Stock();reply=IndiResponseParser.Read(fake,"STOCKS","");
            Check(reply.Count==1&&reply.Stocks.Count==1&&reply.Stocks[0].Code=="005930","stock master replay");
            fake.Fields["m:0:7"]="1";Reject(()=>IndiResponseParser.Read(fake,"STOCKS",""),"master with no eligible common stock fails verification");
            fake=new FakeIndi();fake.Bar();reply=IndiResponseParser.Read(fake,"BARS","005930");
            Check(reply.Bars.Count==1&&reply.Bars[0].Close==70000&&reply.Bars[0].Volume==123456,"daily bar replay");
            fake.Fields["m:0:0"]="20260230";Reject(()=>IndiResponseParser.Read(fake,"BARS","005930"),"impossible bar date rejected");
            fake.Bar();fake.Fields["m:0:3"]="60000";Reject(()=>IndiResponseParser.Read(fake,"BARS","005930"),"inconsistent OHLC rejected");
            fake=new FakeIndi();fake.Holding();reply=IndiResponseParser.Read(fake,"HOLDINGS","");
            Check(reply.Holdings.Count==1&&reply.Holdings[0].Quantity==10&&reply.Holdings[0].Code=="005930","holding replay");
            fake.Fields["m:0:2"]="bad";Reject(()=>IndiResponseParser.Read(fake,"HOLDINGS",""),"malformed holding quantity cannot disappear as zero");
            fake.Holding();fake.Fields["m:0:2"]="1.5";Reject(()=>IndiResponseParser.Read(fake,"HOLDINGS",""),"fractional domestic shares rejected");
            fake.Rows=2001;Reject(()=>IndiResponseParser.Read(fake,"HOLDINGS",""),"row limit fails rather than truncating holdings");
            fake=new FakeIndi();Check(IndiResponseParser.Read(fake,"HOLDINGS","").Holdings.Count==0,"empty balance supported");
            requests=new IndiRequestCoordinator(fake);BeginQuote(requests);fake.Fail("connection lost");
            Check(requests.Failed&&!requests.Busy,"transport error halts pending request");requests.Dispose();
            return checks;
        }
    }
}
