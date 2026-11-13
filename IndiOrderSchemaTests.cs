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
        static byte[] Definition(string query,string[] input,string[] output,bool unicode=false) {
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
                writer.Write((ushort)0x0207);text(query);text("합성 주문 정의");text("113");text("Q2");writer.Write(new byte[19]);writer.Write((ushort)2);
                foreach(var section in new[]{new{Direction=1,Fields=input},new{Direction=2,Fields=output}}) {
                    folder("Record",section.Direction,1);classTag("CIONodeFolder");folder("RECORD",section.Direction,section.Fields.Length);
                    foreach(string label in section.Fields) {
                        classTag("CIONodeItem");writer.Write((byte)1);text(label);writer.Write((byte)2);writer.Write((byte)section.Direction);writer.Write(0);writer.Write(0);
                        writer.Write((byte)9);writer.Write(20);text("");text("");
                    }
                }
                writer.Flush();return stream.ToArray();
            }
        }
        public static int Run() {
            checks=0;var legacy=new[]{"주문번호","ORC주문번호","메세지구분","메세지1","메세지2","메세지3"};
            var modern=new[]{"SOR주문번호","KRX주문번호","NXT주문번호","ORC주문번호","메세지구분","메세지1","메세지2","메세지3"};
            byte[] old=Definition("SABA101U1",Inputs(36),legacy);
            var info=IndiOrderSchema.Inspect(old,"SABA101U1");
            Check(info.Kind==IndiOrderSchemaKind.Legacy&&info.InputCount==36&&info.OutputCount==6,"declared legacy input/output records are parsed structurally");
            Check(info.Sha256!=null&&info.Sha256.Length==64&&!info.Diagnostic.Contains("합성"),"diagnostic contains fingerprint and finite metadata rather than resource contents");
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
            var shuffled=(string[])modern.Clone();shuffled[0]="KRX주문번호";shuffled[1]="SOR주문번호";
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",input,shuffled),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"order-ID label order cannot be guessed from the set of strings");
            var bad=(string[])input.Clone();bad[7]="원주문번호";
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",bad,modern),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"changed input index meanings invalidate recognition");
            Check(IndiOrderSchema.Inspect(current,"SABA102U1").Kind==IndiOrderSchemaKind.Unknown,"query identity inside the file must match its fixed requested path");
            Check(IndiOrderSchema.Inspect(Definition("SABA101U1",Inputs(36),modern),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"output-only changes do not fabricate a verified modern schema");
            foreach(int length in new[]{1,20,49,100,old.Length-1})Check(IndiOrderSchema.Inspect(old.Take(length).ToArray(),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"truncated serialization fails closed");
            Check(IndiOrderSchema.Inspect(old.Concat(new byte[]{1}).ToArray(),"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"unparsed trailing nodes are rejected");
            Check(IndiOrderSchema.Inspect(new byte[1024*1024+1],"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"definition size is bounded before parsing");
            Check(IndiOrderSchema.Inspect(old,"../SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"query names are a fixed allowlist");
            var directory=Path.Combine(Path.GetTempPath(),"OrderSchema-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try {
                var starter=Path.Combine(directory,IndiInstallation.StarterFileName);Directory.CreateDirectory(Path.Combine(directory,"qry"));Directory.CreateDirectory(Path.Combine(directory,"config"));
                File.WriteAllBytes(starter,new byte[0]);File.WriteAllBytes(Path.Combine(directory,"qry","saba101u1.xtr"),current);File.WriteAllBytes(Path.Combine(directory,"config","helpertrinfo.dat"),helper);
                info=IndiOrderSchema.ReadInstalled(starter,"SABA101U1");Check(info.Kind==IndiOrderSchemaKind.Modern&&info.KrxRouteCode=="1","only fixed query/helper files under a verified starter directory are read");
                Check(!info.Diagnostic.Contains(directory)&&File.ReadAllBytes(Path.Combine(directory,"qry","saba101u1.xtr")).SequenceEqual(current),"installed definition inspection exposes no path and changes no bytes");
                Check(IndiOrderSchema.ReadInstalled(IndiInstallation.StarterFileName,"SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"relative starter cannot select a local query");
                Check(IndiOrderSchema.ReadInstalled(starter,"../SABA101U1").Kind==IndiOrderSchemaKind.Unknown,"query traversal never selects a file");
            }finally{Directory.Delete(directory,true);}
            return checks;
        }
    }
}
