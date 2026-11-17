using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShinhanNativeTrader {
    public enum IndiAccountOrderSchemaKind { Unknown,Modern0208 }

    public sealed class IndiAccountOrderSchemaInfo {
        public IndiAccountOrderSchemaKind Kind {get;private set;}
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
        public bool Recognized {get{return Kind==IndiAccountOrderSchemaKind.Modern0208&&QueryName=="SABA231Q1"
            &&SerializationVersion==0x0208&&InputCount==9&&SingleOutputCount==6&&MultiOutputCount==54
            &&FailureCode=="Recognized"&&ParseStage=="Complete"&&ParseOffset==SizeBytes&&SizeBytes>0
            &&Sha256!=null&&Sha256.Length==64&&Sha256.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));}}
        internal IndiAccountOrderSchemaInfo(IndiAccountOrderSchemaKind kind,int version,int size,int input,int single,int multi,
            string digest,string failure,string stage,int offset) {
            Kind=kind;QueryName="SABA231Q1";SerializationVersion=version;SizeBytes=size;InputCount=input;SingleOutputCount=single;
            MultiOutputCount=multi;Sha256=digest;FailureCode=failure;ParseStage=stage;ParseOffset=offset;
            Diagnostic="Account order schema: SABA231Q1; status="+failure+"; kind="+kind
                +"; bytes="+size.ToString(CultureInfo.InvariantCulture)+"; SHA256="+(digest??"Unavailable")
                +"; stage="+stage+"; offset="+offset.ToString(CultureInfo.InvariantCulture)
                +"; format="+version.ToString("X4",CultureInfo.InvariantCulture)
                +"; inputs="+input.ToString(CultureInfo.InvariantCulture)+"; single="+single.ToString(CultureInfo.InvariantCulture)
                +"; multi="+multi.ToString(CultureInfo.InvariantCulture);
        }
    }

    // Read-only structural recognition of one installed query definition. This
    // proves a field profile, never that any returned status value means success.
    // Resource strings, local paths and account information are never diagnosed.
    public static class IndiAccountOrderSchema {
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
            new FieldSpec("매매일자","ord_ymd",9,8,0),
            new FieldSpec("계좌번호","acct_no",9,11,1002),
            new FieldSpec("비밀번호","acct_pwd",18,9,0),
            new FieldSpec("장구분","mrkt_deal_tp_code",9,2,0),
            new FieldSpec("체결구분","cntr_tp_code",9,1,0),
            new FieldSpec("건별구분","adup_tp_code",9,1,0),
            new FieldSpec("입력종목코드","stbd_code",9,12,0),
            new FieldSpec("계좌상품코드","acct_gds_code",9,2,0),
            new FieldSpec("작업구분","job_tp",9,1,0),
        };
        static readonly FieldSpec[] SingleFields=new[]{
            new FieldSpec("매도체결금액","sell_amt",16,20,0),
            new FieldSpec("매수체결금액","buy_amt",16,20,0),
            new FieldSpec("약정금액","engg_amt",16,20,0),
            new FieldSpec("환산약정","media_ptcf_engg_amt",16,20,0),
            new FieldSpec("체결율","cntr_rt",16,20,0),
            new FieldSpec("계좌관리부점코드","acct_mang_dbrn_code",9,3,0),
        };
        static readonly FieldSpec[] MultiFields=new[]{
            new FieldSpec("주문번호","ord_no",16,18,0),
            new FieldSpec("원주문번호","orig_ord_no",16,18,0),
            new FieldSpec("시장거래구분","mrkt_tp_code",9,1,0),
            new FieldSpec("시장구분명","mrkt_tp_nm",9,20,0),
            new FieldSpec("매수매도구분","sell_buy_tp_code",9,1,0),
            new FieldSpec("매매구분명","ord_type_nm",9,40,0),
            new FieldSpec("정/취 구분","mdfy_cncl_tp_code",9,1,0),
            new FieldSpec("정/취 구분면","mdfy_cncl_tp_nm",9,30,0),
            new FieldSpec("정규시간외구분코드","regul_tmout_tp_code",9,1,0),
            new FieldSpec("호가유형코드","callv_type_code",9,1,0),
            new FieldSpec("대량매매구분코드","many_trde_tp_code",9,1,0),
            new FieldSpec("자사주신고서ID","sfsk_statm_id",9,5,0),
            new FieldSpec("매체구분명","media_tp_nm",9,20,0),
            new FieldSpec("출력종목코드","stbd_code",9,12,0),
            new FieldSpec("종목명","stbd_nm",9,50,0),
            new FieldSpec("주문수량","ord_qty",16,18,0),
            new FieldSpec("주문단가","ord_uv",16,20,0),
            new FieldSpec("상품구분","acct_gds_code",9,2,0),
            new FieldSpec("주문시간","ord_time",9,6,0),
            new FieldSpec("거부코드","rejt_code",9,4,0),
            new FieldSpec("거부코드명","rejt_code_nm",9,40,0),
            new FieldSpec("확인구분","ord_stat_tp_code",9,1,0),
            new FieldSpec("체결시간","cntr_time",9,6,0),
            new FieldSpec("입력자","mang_empc_nm",9,10,0),
            new FieldSpec("체결수량","cntr_qty",16,18,0),
            new FieldSpec("체결단가","cntr_uv",16,20,0),
            new FieldSpec("미체결수량","ord_rstqt",16,18,0),
            new FieldSpec("대출일","lndo_ymd",9,10,0),
            new FieldSpec("확인수량","acpt_qty",16,18,0),
            new FieldSpec("신용구분","crd_deal_tp_code",9,2,0),
            new FieldSpec("가격유형명","trde_tp_nm",9,30,0),
            new FieldSpec("상태명","ord_proc_stat_nm",9,20,0),
            new FieldSpec("현재가","nowv",16,20,0),
            new FieldSpec("주문조건","ord_cond_tp_code",9,1,0),
            new FieldSpec("주문조건명","ord_cond_tp_nm",9,20,0),
            new FieldSpec("자동취소수량","ord_cond_qty",16,18,0),
            new FieldSpec("운용사지시번호","operco_idct_no",9,12,0),
            new FieldSpec("공매도구분코드","empt_tp_code",9,2,0),
            new FieldSpec("매매단위수량","trde_unit_qty",16,18,0),
            new FieldSpec("사이버ID","cyb_id",9,12,0),
            new FieldSpec("신용상환조건코드","crd_rpy_cond_code",9,1,0),
            new FieldSpec("주문방식구분코드","ord_meth_tp_code",9,1,0),
            new FieldSpec("주문방식구분명","ord_meth_tp_nm",9,10,0),
            new FieldSpec("주문거래소구분코드","ordexg_tp_code",9,1,0),
            new FieldSpec("주문거래소구분명","ordexg_tp_nm",9,40,0),
            new FieldSpec("주문조건가격","ord_cond_price",16,20,0),
            new FieldSpec("정지주문효력발생여부","stop_ord_efct_ocr_yn",9,1,0),
            new FieldSpec("SOR주문번호","sor_ord_no",16,18,0),
            new FieldSpec("SOR원주문번호","sor_orig_ord_no",16,18,0),
            new FieldSpec("SOR정정취소구분명","sor_mdfy_cncl_tp_nm",9,30,0),
            new FieldSpec("SOR주문연결해지여부","sor_ord_link_abnd_yn",9,1,0),
            new FieldSpec("정규시간외구분명","",9,30,0),
            new FieldSpec("NXT정규시장구분코드","",9,1,0),
            new FieldSpec("NXT정규시장구분명","",9,100,0),
        };

        static string Canonical(string value){return value.Replace(" ","");}
        static bool Match(IList<FieldSpec> fields,IList<FieldSpec> expected) {
            return fields.Count==expected.Count&&fields.Select((field,index)=>
                Canonical(field.Name)==Canonical(expected[index].Name)&&field.Alias==expected[index].Alias
                &&field.Type==expected[index].Type&&field.Width==expected[index].Width&&field.Fid==expected[index].Fid).All(x=>x);
        }
        static IndiAccountOrderSchemaInfo Unavailable() {
            return new IndiAccountOrderSchemaInfo(IndiAccountOrderSchemaKind.Unknown,0,0,0,0,0,null,"Unavailable","ReadDefinition",0);
        }
        public static IndiAccountOrderSchemaInfo ReadInstalled(string starterPath) {
            try {
                if(string.IsNullOrWhiteSpace(starterPath)||starterPath.IndexOf('"')>=0||starterPath.Any(char.IsControl)
                    ||!Path.IsPathRooted(starterPath))return Unavailable();
                string starter=Path.GetFullPath(starterPath);
                if(!string.Equals(Path.GetFileName(starter),IndiInstallation.StarterFileName,StringComparison.OrdinalIgnoreCase)
                    ||!File.Exists(starter))return Unavailable();
                string root=Path.GetDirectoryName(starter);
                if(string.IsNullOrEmpty(root))return Unavailable();
                byte[] bytes;
                using(var stream=new FileStream(Path.Combine(root,"qry","saba231q1.xtr"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(stream.Length<=0||stream.Length>MaximumLength)return Unavailable();
                    bytes=new byte[(int)stream.Length];int offset=0;
                    while(offset<bytes.Length){int count=stream.Read(bytes,offset,bytes.Length-offset);if(count<=0)return Unavailable();offset+=count;}
                    if(stream.ReadByte()!=-1)return Unavailable();
                }
                return Inspect(bytes);
            }catch{return Unavailable();}
        }
        public static IndiAccountOrderSchemaInfo Inspect(byte[] bytes) {
            int size=bytes==null?0:bytes.Length,version=0,inputs=0,single=0,multi=0,offset=0;
            string hash=null,stage="Bounds",failure="UnsupportedFormat";
            var kind=IndiAccountOrderSchemaKind.Unknown;
            if(bytes==null||size==0||size>MaximumLength)
                return new IndiAccountOrderSchemaInfo(kind,version,size,inputs,single,multi,hash,failure,stage,offset);
            using(var algorithm=SHA256.Create())hash=BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            var reader=new DefinitionReader(bytes);
            try {
                stage="Header";version=reader.Word();if(version!=0x0208)throw new InvalidDataException();
                stage="Identity";
                if(reader.String()!="SABA231Q1")throw new InvalidDataException();
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
                kind=IndiAccountOrderSchemaKind.Modern0208;failure="Recognized";stage="Complete";
            }catch { /* Fixed stages and offsets only; never provider/resource exception text. */ }
            offset=reader.Position;
            return new IndiAccountOrderSchemaInfo(kind,version,size,inputs,single,multi,hash,failure,stage,offset);
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
                if(Byte()!=1||String()!=expected||Byte()!=1||Byte()!=direction||Word()!=marker||Dword()!=0||Dword()!=0)
                    throw new InvalidDataException();
                int count=Word();if(count>128)throw new InvalidDataException();return count;
            }
            public List<FieldSpec> Fields(int direction,int marker) {
                int count=Folder("RECORD",direction,marker);var fields=new List<FieldSpec>();
                for(int index=0;index<count;index++) {
                    ExpectClass("CIONodeItem");if(Byte()!=2)throw new InvalidDataException();
                    string label=String();
                    if(Byte()!=2||Byte()!=direction||Dword()!=0)throw new InvalidDataException();
                    uint fid=Dword();byte type=Byte();uint width=Dword();
                    if(type!=9&&type!=16&&type!=18||width==0||width>4096)throw new InvalidDataException();
                    string alias=String();String();if(String()!="")throw new InvalidDataException();
                    fields.Add(new FieldSpec(label,alias,type,width,fid));
                }
                return fields;
            }
        }
    }
}

