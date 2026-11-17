using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShinhanNativeTrader {
    public enum IndiHoldingsSchemaKind { Unknown,Modern0208 }

    public sealed class IndiHoldingsSchemaInfo {
        public IndiHoldingsSchemaKind Kind {get;private set;}
        public string QueryName {get;private set;}
        public int SerializationVersion {get;private set;}
        public int SizeBytes {get;private set;}
        public int InputCount {get;private set;}
        public int SingleOutputCount {get;private set;}
        public int MultiOutputCount {get;private set;}
        public string Sha256 {get;private set;}
        public string FailureCode {get;private set;}
        public string ParseStage {get;private set;}
        public int ParseOffset {get;private set;}
        public string Diagnostic {get;private set;}
        public bool Recognized {get{return Kind==IndiHoldingsSchemaKind.Modern0208&&QueryName=="SABA609Q1"
            &&SerializationVersion==0x0208&&InputCount==8&&SingleOutputCount==37&&MultiOutputCount==39
            &&FailureCode=="Recognized"&&ParseStage=="Complete"&&ParseOffset==SizeBytes&&SizeBytes>0
            &&Sha256!=null&&Sha256.Length==64&&Sha256.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));}}
        internal IndiHoldingsSchemaInfo(IndiHoldingsSchemaKind kind,int version,int size,int input,int single,int multi,
            string digest,string failure,string stage,int offset) {
            Kind=kind;QueryName="SABA609Q1";SerializationVersion=version;SizeBytes=size;InputCount=input;SingleOutputCount=single;
            MultiOutputCount=multi;Sha256=digest;FailureCode=failure;ParseStage=stage;ParseOffset=offset;
            Diagnostic="Holdings schema: SABA609Q1; status="+failure+"; kind="+kind
                +"; bytes="+size.ToString(CultureInfo.InvariantCulture)+"; SHA256="+(digest??"Unavailable")
                +"; stage="+stage+"; offset="+offset.ToString(CultureInfo.InvariantCulture)
                +"; format="+version.ToString("X4",CultureInfo.InvariantCulture)
                +"; inputs="+input.ToString(CultureInfo.InvariantCulture)+"; single="+single.ToString(CultureInfo.InvariantCulture)
                +"; multi="+multi.ToString(CultureInfo.InvariantCulture);
        }
    }

    // Read-only structural recognition of the expanded balance definition. This
    // proves field positions, never that a runtime quantity or valuation is valid.
    // Resource strings, local paths and account information are never diagnosed.
    public static class IndiHoldingsSchema {
        const int MaximumLength=1024*1024;
        static readonly Encoding Ansi=Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
        static readonly Encoding Unicode=new UnicodeEncoding(false,false,true);
        sealed class FieldSpec {
            public readonly string Name,Alias;
            public readonly byte Type;
            public readonly uint Width,Fid;
            public FieldSpec(string name,string alias,byte type,uint width,uint fid){Name=name;Alias=alias;Type=type;Width=width;Fid=fid;}
        }
        static readonly FieldSpec[] InputFields=new[]{
            new FieldSpec("종합계좌번호","acct_no",9,11,1002),
            new FieldSpec("상품구분","acct_gds_code",9,2,0),
            new FieldSpec("비밀번호","acct_pwd",18,9,0),
            new FieldSpec("구분1","qry_tp_code",9,1,0),
            new FieldSpec("단가구분","uv_tp_code",9,1,0),
            new FieldSpec("종목구분코드","stbd_tp_code",9,1,0),
            new FieldSpec("작업구분코드","job_tp_code",9,1,0),
            new FieldSpec("조회시세구분코드","qry_quot_tp_code",9,1,0),
        };
        static readonly FieldSpec[] SingleFields=new[]{
            new FieldSpec("일자","tdy",9,2,0),
            new FieldSpec("요일(D)","tdy_day",9,2,0),
            new FieldSpec("예수금(D)","dpos",16,20,0),
            new FieldSpec("일자(D+1)","n1d",9,2,0),
            new FieldSpec("요일(D+1)","n1d_day",9,2,0),
            new FieldSpec("예수금(D+1)","d_pls_dd1_psum_dpos",16,20,0),
            new FieldSpec("일자(D+2)","n2d",9,2,0),
            new FieldSpec("요일(D+2)","n2d_day",9,2,0),
            new FieldSpec("예수금(D+2)","d_pls_dd2_psum_dpos",16,20,0),
            new FieldSpec("인출가능금액(D)","drw_aval_amt",16,20,0),
            new FieldSpec("신용금액(융자)","loan_amt",16,20,0),
            new FieldSpec("대출금액","lndo_amt",16,20,0),
            new FieldSpec("현금매수","ord_aval_cash",16,20,0),
            new FieldSpec("최대(미수)","max_ord_aval_amt",16,20,0),
            new FieldSpec("신용매수","crd_ord_aval_amt",16,20,0),
            new FieldSpec("주식매입금액","stk_buy_amt",16,20,0),
            new FieldSpec("실현손익","actl_pl_amt",16,20,0),
            new FieldSpec("채권평가금","bd_evlt_amt",16,20,0),
            new FieldSpec("주식평가금액","stk_evlt_amt",16,20,0),
            new FieldSpec("미실현손익","nactl_pl_amt",16,20,0),
            new FieldSpec("현금증거금","cash_mrgn",16,20,0),
            new FieldSpec("위탁순자산평가","trst_passt_evlt_amt",16,20,0),
            new FieldSpec("미실현손익률","nactl_pl_rt",16,20,0),
            new FieldSpec("익일반대매매","n1d_opps_trde_amt",16,20,0),
            new FieldSpec("총계약금액","cntc_amt",16,20,0),
            new FieldSpec("납입금액","pdin_amt",16,20,0),
            new FieldSpec("납입횟수","pdin_numt",9,18,0),
            new FieldSpec("채권매입금액","bd_buy_amt",16,20,0),
            new FieldSpec("상품순자산평가","gds_passt_evlt_amt",16,20,0),
            new FieldSpec("채권손익률","bd_pl_rt",16,20,0),
            new FieldSpec("대주가능금액","ls_ord_aval_amt",16,20,0),
            new FieldSpec("매입대출금액","purc_fnd_ord_aval_amt",16,20,0),
            new FieldSpec("대출만기일자","expr_ymd",9,8,0),
            new FieldSpec("세후RP금액","cma_rp_atax_evlt_amt",16,20,0),
            new FieldSpec("수시RP금액","anytm_rp_atax_evlt_amt",16,20,0),
            new FieldSpec("대주금액","ls_amt",16,20,0),
            new FieldSpec("융자_대주금액","crd_ls_amt_sumtot",16,20,0),
        };
        static readonly FieldSpec[] MultiFields=new[]{
            new FieldSpec("종목코드","stbd_code",9,12,11),
            new FieldSpec("종목명","stbd_nm",9,50,0),
            new FieldSpec("구분코드","qry_tp_code",9,1,0),
            new FieldSpec("구분","trde_tp_nm",9,10,0),
            new FieldSpec("잔고수량","remq_qty",16,18,0),
            new FieldSpec("전일매도미결제","p1d_sell_qty",16,18,0),
            new FieldSpec("당일매도미결제","tdy_sell_qty",16,18,0),
            new FieldSpec("전일매수미결제","p1d_buy_qty",16,18,0),
            new FieldSpec("당일매수미결제","tdy_buy_qty",16,18,0),
            new FieldSpec("결제일수량","dcd_qty",16,18,0),
            new FieldSpec("주문가능수량","ord_aval_qty",16,18,0),
            new FieldSpec("미체결수량","ncntr_qty",16,18,0),
            new FieldSpec("평균단가","buy_uv",16,20,0),
            new FieldSpec("현재가","nowv",16,20,470),
            new FieldSpec("전일대비","p1d_diff_callv",16,20,401),
            new FieldSpec("비율","uv_diff_ratio",16,20,402),
            new FieldSpec("매도호가","sell_callv",16,20,650),
            new FieldSpec("매수호가","buy_callv",16,20,702),
            new FieldSpec("평가금액","evlt_amt",16,20,0),
            new FieldSpec("미실현손익금액","nactl_pl_amt",16,20,0),
            new FieldSpec("손익률","nactl_pl_rt",16,20,0),
            new FieldSpec("보유비중","hold_ratio",16,20,0),
            new FieldSpec("융자금액","flow_amt",16,20,0),
            new FieldSpec("자기금액","self_amt",16,20,0),
            new FieldSpec("대출일","lndo_ymd",9,8,0),
            new FieldSpec("만기일","expr_ymd",9,8,0),
            new FieldSpec("신용이자","crd_int",16,20,0),
            new FieldSpec("매수일자(채권)","buy_ymd",9,8,0),
            new FieldSpec("단축코드","rdcn_code",9,12,11),
            new FieldSpec("전일종가","p1d_closv",16,20,0),
            new FieldSpec("대주손익계산구분","ls_yn",9,1,0),
            new FieldSpec("질권설정수량","pldg_estb_qty",16,18,0),
            new FieldSpec("평가잔고수량","evlt_base_qty",16,18,0),
            new FieldSpec("채권구분","bd_scrn_tp_code",9,1,0),
            new FieldSpec("매매수량단위","trde_qty_unit",16,18,0),
            new FieldSpec("매수수수료","buy_cmsn",9,20,0),
            new FieldSpec("매도수수료","sell_cmsn",9,20,0),
            new FieldSpec("거래세","trtax",9,20,0),
            new FieldSpec("분리과세구분코드","splt_txon_tp_code",9,1,0),
        };

        static string Canonical(string value){return value.Replace(" ","");}
        static bool Match(IList<FieldSpec> fields,IList<FieldSpec> expected) {
            return fields.Count==expected.Count&&fields.Select((field,index)=>
                Canonical(field.Name)==Canonical(expected[index].Name)&&field.Alias==expected[index].Alias
                &&field.Type==expected[index].Type&&field.Width==expected[index].Width&&field.Fid==expected[index].Fid).All(x=>x);
        }
        static IndiHoldingsSchemaInfo Unavailable() {
            return new IndiHoldingsSchemaInfo(IndiHoldingsSchemaKind.Unknown,0,0,0,0,0,null,"Unavailable","ReadDefinition",0);
        }
        public static IndiHoldingsSchemaInfo ReadInstalled(string starterPath) {
            try {
                if(string.IsNullOrWhiteSpace(starterPath)||starterPath.IndexOf('"')>=0||starterPath.Any(char.IsControl)
                    ||!Path.IsPathRooted(starterPath))return Unavailable();
                string starter=Path.GetFullPath(starterPath);
                if(!string.Equals(Path.GetFileName(starter),IndiInstallation.StarterFileName,StringComparison.OrdinalIgnoreCase)
                    ||!File.Exists(starter))return Unavailable();
                string root=Path.GetDirectoryName(starter);
                if(string.IsNullOrEmpty(root))return Unavailable();
                byte[] bytes;
                using(var stream=new FileStream(Path.Combine(root,"qry","saba609q1.xtr"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(stream.Length<=0||stream.Length>MaximumLength)return Unavailable();
                    bytes=new byte[(int)stream.Length];int offset=0;
                    while(offset<bytes.Length){int count=stream.Read(bytes,offset,bytes.Length-offset);if(count<=0)return Unavailable();offset+=count;}
                    if(stream.ReadByte()!=-1)return Unavailable();
                }
                return Inspect(bytes);
            }catch{return Unavailable();}
        }
        public static IndiHoldingsSchemaInfo Inspect(byte[] bytes) {
            int size=bytes==null?0:bytes.Length,version=0,inputs=0,single=0,multi=0,offset=0;
            string hash=null,stage="Bounds",failure="UnsupportedFormat";
            var kind=IndiHoldingsSchemaKind.Unknown;
            if(bytes==null||size==0||size>MaximumLength)
                return new IndiHoldingsSchemaInfo(kind,version,size,inputs,single,multi,hash,failure,stage,offset);
            using(var algorithm=SHA256.Create())hash=BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            var reader=new DefinitionReader(bytes);
            try {
                stage="Header";version=reader.Word();if(version!=0x0208)throw new InvalidDataException();
                stage="Identity";
                if(reader.String()!="SABA609Q1")throw new InvalidDataException();
                reader.String();if(reader.String()!="112"||reader.String()!="Q2")throw new InvalidDataException();
                stage="RecordHeader";reader.Skip(19);if(reader.Word()!=2)throw new InvalidDataException();
                stage="InputRecord";
                if(reader.Folder("Record",1,256)!=1)throw new InvalidDataException();
                reader.ExpectClass("CIONodeFolder");var input=reader.Fields(1,256);inputs=input.Count;
                stage="OutputRecord";
                if(reader.Folder("Record",2,256)!=2)throw new InvalidDataException();
                reader.ExpectClass("CIONodeFolder");var outputSingle=reader.Fields(2,256);single=outputSingle.Count;
                reader.ExpectClass("CIONodeFolder");var outputMulti=reader.Fields(2,257);multi=outputMulti.Count;
                stage="Trailing";if(!reader.End){failure="TrailingData";throw new InvalidDataException();}
                failure="UnsupportedSchema";stage="InputSemantics";if(!Match(input,InputFields))throw new InvalidDataException();
                stage="SingleSemantics";if(!Match(outputSingle,SingleFields))throw new InvalidDataException();
                stage="MultiSemantics";if(!Match(outputMulti,MultiFields))throw new InvalidDataException();
                kind=IndiHoldingsSchemaKind.Modern0208;failure="Recognized";stage="Complete";
            }catch { /* Fixed stages and offsets only; never provider/resource exception text. */ }
            offset=reader.Position;
            return new IndiHoldingsSchemaInfo(kind,version,size,inputs,single,multi,hash,failure,stage,offset);
        }
        sealed class DefinitionReader {
            readonly byte[] bytes;int position,nextClassIndex=1;
            readonly Dictionary<int,string> classes=new Dictionary<int,string>();
            public DefinitionReader(byte[] bytes){this.bytes=bytes;}
            public int Position {get{return position;}}
            public bool End {get{return position==bytes.Length;}}
            public void Skip(int count){if(count<0||position>bytes.Length-count)throw new InvalidDataException();position+=count;}
            byte Byte(){if(position>=bytes.Length)throw new InvalidDataException();return bytes[position++];}
            public ushort Word(){return (ushort)(Byte()|(Byte()<<8));}
            uint Dword(){return (uint)(Word()|((uint)Word()<<16));}
            static int Length32(uint value){if(value>2048)throw new InvalidDataException();return (int)value;}
            int Length(out bool wide) {
                wide=false;int count=Byte();if(count<255)return count;
                count=Word();if(count==65534){wide=true;count=Byte();if(count<255)return count;count=Word();}
                return count<65535?count:Length32(Dword());
            }
            public string String() {
                bool wide;int count=Length(out wide);if(count>2048)throw new InvalidDataException();
                int length=wide?checked(count*2):count,start=position;Skip(length);
                return (wide?Unicode:Ansi).GetString(bytes,start,length);
            }
            public void ExpectClass(string expected) {
                ushort tag=Word();string name;
                if(tag==65535) {
                    if(Word()!=1)throw new InvalidDataException();
                    int count=Word();if(count<=0||count>40)throw new InvalidDataException();
                    int start=position;Skip(count);name=Encoding.ASCII.GetString(bytes,start,count);
                    classes.Add(nextClassIndex++,name);
                }else if((tag&32768)==0||!classes.TryGetValue(tag&32767,out name))throw new InvalidDataException();
                nextClassIndex++;if(name!=expected)throw new InvalidDataException();
            }
            public int Folder(string expected,int direction,int marker) {
                if(Byte()!=1||String()!=expected||Byte()!=1||Byte()!=direction||Word()!=marker||Dword()!=0)
                    throw new InvalidDataException();
                uint links=Dword();
                if(marker==257) {
                    if(links!=2)throw new InvalidDataException();
                    foreach(var kind in new[]{"SC","WC"}) {
                        if(Word()!=1||String()!=kind||Dword()!=0||Word()!=1||String()!="단축코드"||Word()!=6)
                            throw new InvalidDataException();
                        foreach(var field in new[]{"매도호가","매수호가","비율","전일대비","전일대비구분","현재가"})
                            if(String()!=field)throw new InvalidDataException();
                        if(Dword()!=0x00ffffff||Word()!=0)throw new InvalidDataException();
                    }
                }else if(links!=0)throw new InvalidDataException();
                int count=Word();if(count>128)throw new InvalidDataException();return count;
            }
            public List<FieldSpec> Fields(int direction,int marker) {
                int count=Folder("RECORD",direction,marker);var fields=new List<FieldSpec>();
                for(int index=0;index<count;index++) {
                    ExpectClass("CIONodeItem");if(Byte()!=1)throw new InvalidDataException();
                    string label=String();
                    if(Byte()!=2||Byte()!=direction||Dword()!=0)throw new InvalidDataException();
                    uint fid=Dword();byte type=Byte();uint width=Dword();
                    if(type!=9&&type!=16&&type!=18||width==0||width>4096)throw new InvalidDataException();
                    string alias=String();String();
                    fields.Add(new FieldSpec(label,alias,type,width,fid));
                }
                return fields;
            }
        }
    }
}

