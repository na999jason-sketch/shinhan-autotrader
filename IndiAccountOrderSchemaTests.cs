using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ShinhanNativeTrader {
    public static class IndiAccountOrderSchemaTests {
        sealed class Field {
            public string Name,Alias,Default="",Extra="";
            public byte Type;
            public uint Width,Fid;
            public Field(string name,string alias,byte type,uint width,uint fid){Name=name;Alias=alias;Type=type;Width=width;Fid=fid;}
        }
        sealed class Fixture {
            public ushort Version=0x0208,ClassVersion=1,InputMarker=256,SingleMarker=256,MultiMarker=257;
            public byte ItemVersion=2,OutputDirection=2;
            public string Query="SABA231Q1",Description="합성 계좌 주문체결 정의",Service="112",Protocol="Q2",ItemClass="CIONodeItem";
            public bool WideStrings;
            public readonly List<Field> Input=new List<Field>{
                new Field("매매일자","ord_ymd",9,8,0),
                new Field("계좌번호","acct_no",9,11,1002),
                new Field("비밀번호","acct_pwd",18,9,0),
                new Field("장구분","mrkt_deal_tp_code",9,2,0),
                new Field("체결구분","cntr_tp_code",9,1,0),
                new Field("건별구분","adup_tp_code",9,1,0),
                new Field("입력종목코드","stbd_code",9,12,0),
                new Field("계좌상품코드","acct_gds_code",9,2,0),
                new Field("작업구분","job_tp",9,1,0),
            };
            public readonly List<Field> Single=new List<Field>{
                new Field("매도체결금액","sell_amt",16,20,0),
                new Field("매수체결금액","buy_amt",16,20,0),
                new Field("약정금액","engg_amt",16,20,0),
                new Field("환산약정","media_ptcf_engg_amt",16,20,0),
                new Field("체결율","cntr_rt",16,20,0),
                new Field("계좌관리부점코드","acct_mang_dbrn_code",9,3,0),
            };
            public readonly List<Field> Multi=new List<Field>{
                new Field("주문번호","ord_no",16,18,0),
                new Field("원주문번호","orig_ord_no",16,18,0),
                new Field("시장거래구분","mrkt_tp_code",9,1,0),
                new Field("시장구분명","mrkt_tp_nm",9,20,0),
                new Field("매수매도구분","sell_buy_tp_code",9,1,0),
                new Field("매매구분명","ord_type_nm",9,40,0),
                new Field("정/취 구분","mdfy_cncl_tp_code",9,1,0),
                new Field("정/취 구분면","mdfy_cncl_tp_nm",9,30,0),
                new Field("정규시간외구분코드","regul_tmout_tp_code",9,1,0),
                new Field("호가유형코드","callv_type_code",9,1,0),
                new Field("대량매매구분코드","many_trde_tp_code",9,1,0),
                new Field("자사주신고서ID","sfsk_statm_id",9,5,0),
                new Field("매체구분명","media_tp_nm",9,20,0),
                new Field("출력종목코드","stbd_code",9,12,0),
                new Field("종목명","stbd_nm",9,50,0),
                new Field("주문수량","ord_qty",16,18,0),
                new Field("주문단가","ord_uv",16,20,0),
                new Field("상품구분","acct_gds_code",9,2,0),
                new Field("주문시간","ord_time",9,6,0),
                new Field("거부코드","rejt_code",9,4,0),
                new Field("거부코드명","rejt_code_nm",9,40,0),
                new Field("확인구분","ord_stat_tp_code",9,1,0),
                new Field("체결시간","cntr_time",9,6,0),
                new Field("입력자","mang_empc_nm",9,10,0),
                new Field("체결수량","cntr_qty",16,18,0),
                new Field("체결단가","cntr_uv",16,20,0),
                new Field("미체결수량","ord_rstqt",16,18,0),
                new Field("대출일","lndo_ymd",9,10,0),
                new Field("확인수량","acpt_qty",16,18,0),
                new Field("신용구분","crd_deal_tp_code",9,2,0),
                new Field("가격유형명","trde_tp_nm",9,30,0),
                new Field("상태명","ord_proc_stat_nm",9,20,0),
                new Field("현재가","nowv",16,20,0),
                new Field("주문조건","ord_cond_tp_code",9,1,0),
                new Field("주문조건명","ord_cond_tp_nm",9,20,0),
                new Field("자동취소수량","ord_cond_qty",16,18,0),
                new Field("운용사지시번호","operco_idct_no",9,12,0),
                new Field("공매도구분코드","empt_tp_code",9,2,0),
                new Field("매매단위수량","trde_unit_qty",16,18,0),
                new Field("사이버ID","cyb_id",9,12,0),
                new Field("신용상환조건코드","crd_rpy_cond_code",9,1,0),
                new Field("주문방식구분코드","ord_meth_tp_code",9,1,0),
                new Field("주문방식구분명","ord_meth_tp_nm",9,10,0),
                new Field("주문거래소구분코드","ordexg_tp_code",9,1,0),
                new Field("주문거래소구분명","ordexg_tp_nm",9,40,0),
                new Field("주문조건가격","ord_cond_price",16,20,0),
                new Field("정지주문효력발생여부","stop_ord_efct_ocr_yn",9,1,0),
                new Field("SOR주문번호","sor_ord_no",16,18,0),
                new Field("SOR원주문번호","sor_orig_ord_no",16,18,0),
                new Field("SOR정정취소구분명","sor_mdfy_cncl_tp_nm",9,30,0),
                new Field("SOR주문연결해지여부","sor_ord_link_abnd_yn",9,1,0),
                new Field("정규시간외구분명","",9,30,0),
                new Field("NXT정규시장구분코드","",9,1,0),
                new Field("NXT정규시장구분명","",9,100,0),
            };

        }
        // Shared test factory emits a synthetic resource with known contract
        // metadata, never a copied/encoded vendor resource or customer data.
        internal static byte[] ModernDefinition(){return Encode(new Fixture());}
        static byte[] Changed(Action<Fixture> change){var fixture=new Fixture();change(fixture);return Encode(fixture);}
        static byte[] Encode(Fixture fixture) {
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)) {
                Action<string> text=value=>{
                    byte[] encoded=fixture.WideStrings?Encoding.Unicode.GetBytes(value):Encoding.GetEncoding(949).GetBytes(value);
                    int length=fixture.WideStrings?value.Length:encoded.Length;
                    if(fixture.WideStrings){writer.Write((byte)255);writer.Write((ushort)65534);}
                    if(length<255)writer.Write((byte)length);
                    else {writer.Write((byte)255);writer.Write((ushort)length);}
                    writer.Write(encoded);
                };
                Action<string,int,int,int> folder=(name,direction,marker,count)=>{
                    writer.Write((byte)1);text(name);writer.Write((byte)1);writer.Write((byte)direction);writer.Write((ushort)marker);
                    writer.Write(0);writer.Write(0);writer.Write((ushort)count);
                };
                int next=1;var classes=new Dictionary<string,int>();
                Action<string> tag=name=>{
                    int index;
                    if(!classes.TryGetValue(name,out index)) {
                        classes.Add(name,next++);writer.Write((ushort)65535);writer.Write(fixture.ClassVersion);
                        writer.Write((ushort)name.Length);writer.Write(Encoding.ASCII.GetBytes(name));
                    }else writer.Write((ushort)(32768|index));
                    next++;
                };
                Action<List<Field>,int,int> fields=(values,direction,marker)=>{
                    tag("CIONodeFolder");folder("RECORD",direction,marker,values.Count);
                    foreach(var value in values) {
                        tag(fixture.ItemClass);writer.Write(fixture.ItemVersion);text(value.Name);writer.Write((byte)2);
                        writer.Write((byte)direction);writer.Write(0);writer.Write(value.Fid);writer.Write(value.Type);writer.Write(value.Width);
                        text(value.Alias);text(value.Default);if(fixture.ItemVersion==2)text(value.Extra);
                    }
                };
                writer.Write(fixture.Version);text(fixture.Query);text(fixture.Description);text(fixture.Service);text(fixture.Protocol);
                writer.Write(new byte[19]);writer.Write((ushort)2);
                folder("Record",1,256,1);fields(fixture.Input,1,fixture.InputMarker);
                folder("Record",fixture.OutputDirection,256,2);
                fields(fixture.Single,fixture.OutputDirection,fixture.SingleMarker);fields(fixture.Multi,fixture.OutputDirection,fixture.MultiMarker);
                writer.Flush();return stream.ToArray();
            }
        }
        static int checks;
        static void Check(bool ok,string message){checks++;if(!ok)throw new Exception("IndiAccountOrderSchemaTests: "+message);}
        static void Reject(byte[] bytes,string message){var info=IndiAccountOrderSchema.Inspect(bytes);Check(!info.Recognized&&info.Kind==IndiAccountOrderSchemaKind.Unknown,message);}
        public static int Run() {
            checks=0;var bytes=ModernDefinition();var info=IndiAccountOrderSchema.Inspect(bytes);
            Check(info.Recognized&&info.Kind==IndiAccountOrderSchemaKind.Modern0208&&info.QueryName=="SABA231Q1","synthetic installed modern order-state profile is recognized structurally");
            Check(info.SerializationVersion==0x0208&&info.InputCount==9&&info.SingleOutputCount==6&&info.MultiOutputCount==54,
                "single and multi output folders retain their distinct six and fifty-four field scopes");
            Check(info.ParseOffset==bytes.Length&&info.SizeBytes==bytes.Length&&info.ParseStage=="Complete"&&info.FailureCode=="Recognized",
                "recognition requires complete consumption and exact successful parser state");
            Check(info.Sha256.Length==64&&info.Diagnostic.Length<512&&!info.Diagnostic.Contains("합성"),
                "bounded diagnostic contains digest and fixed parser metadata without resource descriptions");
            Check(IndiAccountOrderSchema.Inspect((byte[])bytes.Clone()).Sha256==info.Sha256,
                "identical bytes produce stable profile provenance");
            Check(IndiAccountOrderSchema.Inspect(Changed(x=>x.WideStrings=true)).Recognized,
                "MFC Unicode CString framing retains the same verified field contract");
            Reject(Changed(x=>x.Version=0x0207),"legacy envelopes do not acquire the modern order-state profile");
            Reject(Changed(x=>x.Query="SABA101U1"),"another query cannot supply order-state schema evidence");
            Reject(Changed(x=>x.Service="113"),"order-state profile requires account read service112");
            Reject(Changed(x=>x.Protocol="Q1"),"unexpected protocol cannot supply the verified profile");
            Reject(Changed(x=>x.ItemVersion=1),"old node serialization cannot be mistaken for modern node version2");
            Reject(Changed(x=>x.ClassVersion=2),"unknown MFC runtime class version is rejected");
            Reject(Changed(x=>x.ItemClass="OtherNode"),"unknown item classes cannot produce known field metadata");
            Reject(Changed(x=>x.InputMarker=257),"input records cannot be relabeled as repeating output");
            Reject(Changed(x=>x.SingleMarker=257),"single output scope marker is checked");
            Reject(Changed(x=>x.MultiMarker=256),"multi output cannot be substituted with a single record");
            Reject(Changed(x=>x.OutputDirection=1),"response folders cannot carry input direction");
            Reject(Changed(x=>x.Input.RemoveAt(8)),"all nine input fields must exist");
            Reject(Changed(x=>x.Single.RemoveAt(5)),"all six single output fields must exist");
            Reject(Changed(x=>x.Multi.RemoveAt(53)),"profile is not inferred from only the first legacy-compatible outputs");
            Reject(Changed(x=>x.Multi.Add(new Field("extra","extra",9,1,0))),"future output counts require their own reviewed profile");
            for(int index=0;index<9;index++) {
                int field=index;
                Reject(Changed(x=>x.Input[field].Alias="wrong"),"each account/date/query input alias is bound at its exact position");
            }
            foreach(int index in new[]{0,1,4,6,13,14,15,16,17,18,19,20,21,22,24,25,26,28,29,31,35,41,43,47,48}) {
                int field=index;
                Reject(Changed(x=>x.Multi[field].Alias="wrong"),"consumed identity, confirmation and venue output aliases are fixed");
            }
            Reject(Changed(x=>x.Multi[21].Name="other"),"confirmation field label is checked without interpreting any returned code");
            Reject(Changed(x=>x.Multi[21].Width=2),"confirmation field width is part of the profile");
            Reject(Changed(x=>x.Multi[21].Type=16),"confirmation field scalar type is part of the profile");
            Reject(Changed(x=>x.Multi[24].Type=9),"filled quantity cannot silently change to another scalar type");
            Reject(Changed(x=>x.Multi[25].Width=19),"fill price width changes invalidate the known profile");
            Reject(Changed(x=>x.Input[1].Fid=0),"account field binding metadata cannot be silently removed");
            Reject(Changed(x=>{var item=x.Multi[21];x.Multi[21]=x.Multi[22];x.Multi[22]=item;}),"field reordering cannot retain a known profile");
            Reject(Changed(x=>x.Multi[53].Extra="unexpected"),"modern node trailer must remain the explicitly supported empty CString");
            Reject(bytes.Concat(new byte[]{0}).ToArray(),"trailing bytes after the last field invalidate profile recognition");
            bool allPrefixesFail=true;
            for(int length=0;length<bytes.Length;length++)if(IndiAccountOrderSchema.Inspect(bytes.Take(length).ToArray()).Recognized){allPrefixesFail=false;break;}
            Check(allPrefixesFail,"every truncated prefix fails closed without an exception escaping the inspector");
            Reject(null,"null resource is unavailable rather than a profile");
            Reject(new byte[1024*1024+1],"schema reads have a fixed one MiB bound");
            var malformed=(byte[])bytes.Clone();malformed[2]=255;malformed[3]=255;malformed[4]=255;
            Reject(malformed,"malformed MFC string length cannot escape bounds or match by substring");
            var poisoned=Changed(x=>{x.Description="SECRET_ACCOUNT_12345678901 PASSWORD_4321";x.Multi[21].Alias="SECRET_ACCOUNT_12345678901";});
            info=IndiAccountOrderSchema.Inspect(poisoned);
            Check(!info.Recognized&&!info.Diagnostic.Contains("SECRET")&&!info.Diagnostic.Contains("12345678901")&&!info.Diagnostic.Contains("PASSWORD"),
                "untrusted definition and exception strings never enter schema diagnostics");
            var directory=Path.Combine(Path.GetTempPath(),"Indi231-SECRET_ACCOUNT-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory,"qry"));
            try {
                var starter=Path.Combine(directory,IndiInstallation.StarterFileName);File.WriteAllText(starter,"synthetic placeholder; never executed");
                File.WriteAllBytes(Path.Combine(directory,"qry","saba231q1.xtr"),bytes);
                info=IndiAccountOrderSchema.ReadInstalled(starter);
                Check(info.Recognized&&info.Sha256==IndiAccountOrderSchema.Inspect(bytes).Sha256,
                    "installed read resolves only the fixed query below the validated starter");
                Check(!info.Diagnostic.Contains(directory)&&!info.Diagnostic.Contains("SECRET_ACCOUNT"),
                    "installed diagnostics never disclose filesystem or account-like path data");
                Check(!IndiAccountOrderSchema.ReadInstalled(Path.Combine(directory,"other.exe")).Recognized,
                    "arbitrary executable paths cannot resolve query schema evidence");
                File.WriteAllBytes(Path.Combine(directory,"qry","saba231q1.xtr"),bytes.Concat(new byte[]{0}).ToArray());
                Check(!IndiAccountOrderSchema.ReadInstalled(starter).Recognized,"installed malformed definitions retain unavailable profile");
                File.Delete(starter);
                Check(!IndiAccountOrderSchema.ReadInstalled(starter).Recognized,"a missing registered-starter candidate is not trusted");
            }finally{Directory.Delete(directory,true);}
            Check(!IndiAccountOrderSchema.ReadInstalled("giexpertstarter.exe").Recognized
                &&!IndiAccountOrderSchema.ReadInstalled(null).Recognized
                &&!IndiAccountOrderSchema.ReadInstalled("bad\\0path").Recognized,
                "relative, null and invalid starter candidates fail without exposing exception details");
            return checks;
        }
    }
}

