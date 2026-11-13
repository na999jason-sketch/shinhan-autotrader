using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ShinhanNativeTrader {
    public static class IndiOrderSchemaTests {
        static int checks;
        static void Check(bool value,string label){checks++;if(!value)throw new Exception("IndiOrderSchemaTests: "+label);}
        static string[] Inputs(int count) {
            var values=Enumerable.Range(0,count).Select(index=>"항목"+index).ToArray();
            foreach(var entry in new Dictionary<int,string>{{0,"계좌번호"},{1,"상품"},{2,"비밀번호"},{5,"선물대용매도구분"},{6,"신용거래구분"},
                {7,"매도매수구분"},{8,"종목코드"},{9,"주문수량"},{10,"주문가격"},{11,"정규시간외구분코드"},{12,"호가유형코드"},{16,"원주문번호"}})values[entry.Key]=entry.Value;
            return values;
        }
        static byte[] Definition(string query,string[] input,string[] output,bool unicode=false,ushort version=0x0207,string routeAlias="ord_meth_tp_code",int routeWidth=1,byte routeType=9) {
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)) {
                Action<string> text=value=> {
                    if(unicode){writer.Write((byte)255);writer.Write((ushort)65534);writer.Write((byte)value.Length);writer.Write(Encoding.Unicode.GetBytes(value));}
                    else{byte[] bytes=Encoding.GetEncoding(949).GetBytes(value);writer.Write((byte)bytes.Length);writer.Write(bytes);}
                };
                Action<string,int,int> folder=(name,direction,count)=>{writer.Write((byte)1);text(name);writer.Write((byte)1);writer.Write((byte)direction);writer.Write((ushort)256);writer.Write(0);writer.Write(0);writer.Write((ushort)count);};
                int next=1;var classes=new Dictionary<string,int>();
                Action<string> classTag=name=> {
                    int number;
                    if(!classes.TryGetValue(name,out number)){classes.Add(name,next++);writer.Write((ushort)65535);writer.Write((ushort)1);writer.Write((ushort)name.Length);writer.Write(Encoding.ASCII.GetBytes(name));}
                    else writer.Write((ushort)(32768|number));next++;
                };
                writer.Write(version);text(query);text("합성 주문 정의");text("113");text("Q2");writer.Write(new byte[19]);writer.Write((ushort)2);
                foreach(var section in new[]{new{Direction=1,Fields=input},new{Direction=2,Fields=output}}) {
                    folder("Record",section.Direction,1);classTag("CIONodeFolder");folder("RECORD",section.Direction,section.Fields.Length);
                    foreach(string label in section.Fields) {
                        classTag("CIONodeItem");writer.Write((byte)1);text(label);writer.Write((byte)2);writer.Write((byte)section.Direction);writer.Write(0);writer.Write(0);
                        bool route=version==0x0208&&label=="주문방식구분코드";
                        string alias=route?routeAlias:version==0x0208&&label=="주문조건가격"?"ord_cond_price":version==0x0208&&label=="SOR원주문번호"?"sor_orig_ord_no":"";
                        int width=route?routeWidth:version==0x0208&&label=="SOR원주문번호"?18:20;
                        writer.Write(route?routeType:(byte)9);writer.Write(width);text(alias);text("");
                    }
                }
                writer.Flush();return stream.ToArray();
            }
        }
        static string[] CurrentInputs(string query) {
            var values=Inputs(query=="SABA101U1"?39:38);
            values[36]="주문조건가격";values[query=="SABA101U1"?37:35]="주문방식구분코드";values[query=="SABA101U1"?38:37]="SOR원주문번호";
            return values;
        }
        public static int Run() {
            checks=0;var legacy=new[]{"주문번호","ORC주문번호","메세지구분","메세지1","메세지2","메세지3"};
            var modern=new[]{"SOR주문번호","KRX주문번호","NXT주문번호","ORC주문번호","메세지구분","메세지1","메세지2","메세지3"};
            byte[] old=Definition("SABA101U1",Inputs(36),legacy);
            var info=IndiOrderSchema.Inspect(old,"SABA101U1");
            Check(info.Kind==IndiOrderSchemaKind.Legacy&&info.InputCount==36&&info.OutputCount==6,"declared legacy input/output records are parsed structurally");
            Check(info.Sha256!=null&&info.Sha256.Length==64&&!info.Diagnostic.Contains("합성"),"diagnostic contains fingerprint and finite metadata rather than resource contents");
            Check(info.QueryName=="SABA101U1"&&info.SizeBytes==old.Length&&info.ParseStage=="Complete"&&info.ParseOffset==old.Length,
                "recognized schema retains fixed source identity size and complete parser position");
            Check(IndiOrderSchema.Inspect(Definition("SABA102U1",Inputs(35),legacy),"SABA102U1").Kind==IndiOrderSchemaKind.Legacy,"legacy amendment input count is separately verified");
            var input=Inputs(38);input[37]="주문방식";byte[] current=Definition("SABA101U1",input,modern,true);
            info=IndiOrderSchema.Inspect(current,"SABA101U1");
            Check(info.Kind==IndiOrderSchemaKind.Modern&&info.InputCount==38&&info.OutputCount==8,"MFC Unicode CString records preserve the exact modern output order");
            Check(!info.KrxRouteInputIndex.HasValue&&info.KrxRouteCode==null,"modern record layout never implies a routing code");
            var helper=Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,합성,0,0,0\r\nSI,37,주문방식,0:SOR^1:KRX^2:NXT,0\r\n*END\r\n");
            info=IndiOrderSchema.Inspect(current,"SABA101U1",helper);
            Check(info.KrxRouteInputIndex==37&&info.KrxRouteCode=="1","only matching official helper SI37 literal KRX mapping establishes a route code");
            Check(IndiOrderSchema.Inspect(current,"SABA101U1",Encoding.GetEncoding(949).GetBytes("*START,SABA102U1,x\nSI,37,주문방식,0:KRX,0\n*END")).KrxRouteCode==null,"another query cannot supply the new-order route");
            Check(IndiOrderSchema.Inspect(current,"SABA101U1",Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,x\nSI,37,주문방식,0:KRX^1:KRX,0\n*END")).KrxRouteCode==null,"ambiguous KRX code mappings are refused");
            Check(IndiOrderSchema.Inspect(current,"SABA101U1",helper.Concat(helper).ToArray()).KrxRouteCode==null,"duplicate query sections cannot establish routing");
            var amend=Inputs(37);amend[35]="주문방식코드";
            var amendHelper=Encoding.GetEncoding(949).GetBytes("*START,SABA102U1,x\nSI,35,주문방식코드,0:KRX^1:NXT,0\n*END");
            info=IndiOrderSchema.Inspect(Definition("SABA102U1",amend,modern),"SABA102U1",amendHelper);
            Check(info.Kind==IndiOrderSchemaKind.Modern&&info.KrxRouteInputIndex==35&&info.KrxRouteCode=="0","amendment route index uses independently verified SI35");
            var actualNew=CurrentInputs("SABA101U1");var actualAmend=CurrentInputs("SABA102U1");
            var modern208=Definition("SABA101U1",actualNew,modern,false,0x0208);
            var amend208=Definition("SABA102U1",actualAmend,modern,false,0x0208);
            var currentHelper=Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,합성,0,0,0\nSI,37,주문방식구분코드,,\n*END\n*START,SABA102U1,합성,0,0,0\nSI,35,주문방식구분코드,0:SOR 1:KRX 2:NXT,\n*END\n");
            info=IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper);
            Check(info.Kind==IndiOrderSchemaKind.Modern&&info.SerializationVersion==0x0208&&info.InputCount==39&&info.OutputCount==8,
                "actual 0208 envelope with 39 new-order inputs and eight venue outputs is structurally recognized");
            Check(info.KrxRouteCode==null,"a blank new-order Helper mapping alone does not guess a route");
            info=IndiOrderSchema.Inspect(amend208,"SABA102U1",currentHelper);
            Check(info.Kind==IndiOrderSchemaKind.Modern&&info.InputCount==38&&info.KrxRouteInputIndex==35&&info.KrxRouteCode=="1"&&info.RouteSource=="OwnHelper",
                "current amendment definition and whitespace-separated Helper mapping establish KRX1");
            info=IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper,amend208);
            Check(info.Kind==IndiOrderSchemaKind.Modern&&info.KrxRouteInputIndex==37&&info.KrxRouteCode=="1"&&info.RouteSource=="PeerMatched",
                "same validated route alias label type and width bind the explicit amendment enum to new orders");
            Check(info.Diagnostic.Contains("format=0208")&&info.Diagnostic.Contains("routeSource=PeerMatched"),"format and route provenance remain visible without resource contents");
            Check(IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper,amend208.Take(amend208.Length-1).ToArray()).KrxRouteCode==null,
                "a truncated peer resource cannot supply a route");
            Check(IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper,Definition("SABA102U1",actualAmend,modern,false,0x0208,"different_domain")).KrxRouteCode==null,
                "another internal alias cannot lend its route enum");
            Check(IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper,Definition("SABA102U1",actualAmend,modern,false,0x0208,"ord_meth_tp_code",2)).KrxRouteCode==null,
                "a wider peer route field does not establish the same scalar domain");
            Check(IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper,Definition("SABA102U1",actualAmend,modern,false,0x0208,"ord_meth_tp_code",1,16)).KrxRouteCode==null,
                "another peer scalar type does not establish a route");
            Check(IndiOrderSchema.Inspect(modern208,"SABA101U1",currentHelper,Definition("SABA102U1",amend,modern)).KrxRouteCode==null,
                "an old envelope cannot supply an unverified cross-query domain");
            var ownHelper=Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,x\nSI,37,주문방식구분코드,0:SOR 1:KRX 2:NXT,\n*END\n*START,SABA102U1,x\nSI,35,주문방식구분코드,0:SOR 1:KRX 2:NXT,\n*END");
            info=IndiOrderSchema.Inspect(modern208,"SABA101U1",ownHelper,amend208);
            Check(info.KrxRouteCode=="1"&&info.RouteSource=="OwnHelper","an explicit own mapping remains the primary route authority");
            var conflictingHelper=Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,x\nSI,37,주문방식구분코드,0:KRX 1:NXT,\n*END\n*START,SABA102U1,x\nSI,35,주문방식구분코드,0:SOR 1:KRX 2:NXT,\n*END");
            info=IndiOrderSchema.Inspect(modern208,"SABA101U1",conflictingHelper,amend208);
            Check(info.KrxRouteCode==null&&info.RouteSource=="Conflict","contradictory explicit own and peer mappings are never silently chosen");
            var duplicateOwn=Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,x\nSI,37,주문방식구분코드,0:KRX 1:KRX,\n*END\n*START,SABA102U1,x\nSI,35,주문방식구분코드,1:KRX,\n*END");
            Check(IndiOrderSchema.Inspect(modern208,"SABA101U1",duplicateOwn,amend208).KrxRouteCode==null,"ambiguous own mappings cannot be hidden by a peer fallback");
            var wrongTail=(string[])actualNew.Clone();wrongTail[38]="원주문번호";
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",wrongTail,modern,false,0x0208),"SABA101U1",currentHelper,amend208).Kind==IndiOrderSchemaKind.Unknown,
                "a missing SOR-original tail cannot masquerade as the actual current profile");
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",input,modern,false,0x0208),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,
                "0208 cannot reuse a guessed 38-field new-order profile");
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",actualNew,modern,false,0x0209),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,
                "another serialization marker is not assumed compatible");
            var shuffled=(string[])modern.Clone();shuffled[0]="KRX주문번호";shuffled[1]="SOR주문번호";
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",input,shuffled),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"order-ID label order cannot be guessed from the set of strings");
            var bad=(string[])input.Clone();bad[7]="원주문번호";
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",bad,modern),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"changed input index meanings invalidate recognition");
            Check(IndiOrderSchema.Inspect(current,"SABA102U1").Kind==IndiOrderSchemaKind.Unknown,"query identity inside the file must match its fixed requested path");
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",Inputs(36),modern),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"output-only changes do not fabricate a verified modern schema");
            foreach(int length in new[]{1,20,49,100,old.Length-1}) {
                var failed=IndiOrderSchema.Inspect(old.Take(length).ToArray(),"SABA101U1");
                Check(failed.Kind==IndiOrderSchemaKind.Unknown,"truncated serialization fails closed");
                Check(failed.QueryName=="SABA101U1"&&failed.SizeBytes==length&&failed.Sha256.Length==64&&failed.ParseOffset<=length
                    &&failed.Diagnostic.Contains("SABA101U1")&&failed.Diagnostic.Contains("SHA256="+failed.Sha256)
                    &&failed.Diagnostic.Contains("stage="+failed.ParseStage)&&failed.Diagnostic.Contains("offset="+failed.ParseOffset),
                    "unsupported serialization retains query size hash and bounded failure location");
            }
            info=IndiOrderSchema.Inspect(old.Take(1).ToArray(),"SABA101U1");
            Check(info.ParseStage=="Header"&&info.ParseOffset==1&&info.FailureCode=="UnsupportedFormat","header failure is distinguishable from a layout semantic mismatch");
            info=IndiOrderSchema.Inspect(Definition("SABA101U1",input,shuffled),"SABA101U1");
            Check(info.ParseStage=="OutputSemantics"&&info.FailureCode=="UnsupportedSchema"&&info.InputCount==38&&info.OutputCount==8,
                "a parsed but unsupported field layout reports counts without accepting its meanings");
            Check(IndiOrderSchema.Inspect(old.Concat(new byte[]{1}).ToArray(),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"unparsed trailing nodes are rejected");
            Check(IndiOrderSchema.Inspect(new byte[1024*1024+1],"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"definition size is bounded before parsing");
            Check(IndiOrderSchema.Inspect(old,"../SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"query names are a fixed allowlist");
            Check(!IndiOrderSchema.Inspect(old,"SECRET12345678901").Diagnostic.Contains("SECRET12345678901"),"unsupported query text is never copied to diagnostics");
            var directory=Path.Combine(Path.GetTempPath(),"OrderSchema-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try {
                var starter=Path.Combine(directory,IndiInstallation.StarterFileName);Directory.CreateDirectory(Path.Combine(directory,"qry"));Directory.CreateDirectory(Path.Combine(directory,"config"));
                File.WriteAllBytes(starter,new byte[0]);File.WriteAllBytes(Path.Combine(directory,"qry","saba101u1.xtr"),current);File.WriteAllBytes(Path.Combine(directory,"config","helpertrinfo.dat"),helper);
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");Check(info.Kind==IndiOrderSchemaKind.Modern&&info.KrxRouteCode=="1","only fixed query/helper files under a verified starter directory are read");
                Check(!info.Diagnostic.Contains(directory)&&File.ReadAllBytes(Path.Combine(directory,"qry","saba101u1.xtr")).SequenceEqual(current),"installed definition inspection exposes no path and changes no bytes");
                Check(info.HelperSource=="Legacy","legacy helper is used only when the primary modern path is unavailable");
                string primaryDirectory=Path.Combine(directory,"qry","config");Directory.CreateDirectory(primaryDirectory);
                string primaryPath=Path.Combine(primaryDirectory,"helpertrinfo.dat");File.WriteAllBytes(primaryPath,helper);
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");
                Check(info.HelperSource=="Primary"&&info.KrxRouteCode=="1","modern qry/config helper takes precedence when both mappings agree");
                File.WriteAllBytes(Path.Combine(directory,"config","helpertrinfo.dat"),Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,x\nSI,37,주문방식,0:KRX^1:NXT,0\n*END"));
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");
                Check(info.HelperSource=="Conflict"&&info.KrxRouteCode==null&&!info.KrxRouteInputIndex.HasValue,
                    "different verified helper mappings invalidate routing rather than silently selecting either value");
                Check(info.Kind==IndiOrderSchemaKind.Modern&&info.Diagnostic.Contains("helper=Conflict")&&!info.Diagnostic.Contains(directory),
                    "helper conflict preserves independently verified output layout and safe source diagnostics");
                File.WriteAllBytes(primaryPath,Encoding.GetEncoding(949).GetBytes("*START,SABA101U1,x\nSI,37,주문방식,0:SOR^1:NXT,0\n*END"));
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");
                Check(info.HelperSource=="Primary"&&info.KrxRouteCode==null,"an existing primary helper without KRX proof cannot borrow an unrelated legacy mapping");
                File.Delete(Path.Combine(directory,"config","helpertrinfo.dat"));File.WriteAllBytes(primaryPath,helper);
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");
                Check(info.HelperSource=="Primary"&&info.KrxRouteCode=="1","primary helper works without a legacy config directory entry");
                File.WriteAllBytes(Path.Combine(directory,"qry","saba101u1.xtr"),modern208);
                File.WriteAllBytes(Path.Combine(directory,"qry","saba102u1.xtr"),amend208);
                File.WriteAllBytes(primaryPath,currentHelper);
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");
                Check(info.Kind==IndiOrderSchemaKind.Modern&&info.SerializationVersion==0x0208&&info.KrxRouteInputIndex==37
                    &&info.KrxRouteCode=="1"&&info.RouteSource=="PeerMatched"&&info.HelperSource=="Primary",
                    "installed new-order inspection binds the independently parsed current amendment file and Helper enum");
                File.Delete(Path.Combine(directory,"qry","saba102u1.xtr"));
                Check(IndiOrderSchema.ReadInstalled(starter,"SABA101U1").KrxRouteCode==null,
                    "missing installed peer definitions cannot be replaced by a cached or guessed enum");
                File.WriteAllBytes(Path.Combine(directory,"qry","saba102u1.xtr"),amend208);
                File.WriteAllBytes(Path.Combine(directory,"config","helpertrinfo.dat"),conflictingHelper);
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");
                Check(info.HelperSource=="Conflict"&&info.KrxRouteCode==null,
                    "a contradictory peer-domain mapping in either installed Helper invalidates the resulting route");
                Check(IndiOrderSchema.ReadInstalled(IndiInstallation.StarterFileName,"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"relative starter cannot select a local query");
                Check(IndiOrderSchema.ReadInstalled(starter,"../SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"query traversal never selects a file");
            }finally{Directory.Delete(directory,true);}
            return checks;
        }
    }
}
