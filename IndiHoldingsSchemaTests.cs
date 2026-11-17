using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ShinhanNativeTrader {
    public static class IndiHoldingsSchemaTests {
        sealed class Field {
            public string Name,Alias,Default="",Extra="";
            public byte Type;
            public uint Width,Fid;
            public Field(string name,string alias,byte type,uint width,uint fid){Name=name;Alias=alias;Type=type;Width=width;Fid=fid;}
        }
        sealed class Fixture {
            public ushort Version=0x0208,ClassVersion=1,InputMarker=256,SingleMarker=256,MultiMarker=257;
            public byte ItemVersion=1,OutputDirection=2;
            public string Query="SABA609Q1",Description="합성 현물 잔고 정의",Service="112",Protocol="Q2",ItemClass="CIONodeItem";
            public bool WideStrings;
            public uint LinkCount=2,LinkColor=0x00ffffff;
            public ushort LinkVersion=1,KeyCount=1,LinkedFieldCount=6,LinkTrailer=0;
            public string KeyName="단축코드";
            public readonly List<string> Links=new List<string>{"SC","WC"};
            public readonly List<string> LinkedNames=new List<string>{"매도호가","매수호가","비율","전일대비","전일대비구분","현재가"};
            public readonly List<Field> Input=new List<Field>{
                new Field("종합계좌번호","acct_no",9,11,1002),
                new Field("상품구분","acct_gds_code",9,2,0),
                new Field("비밀번호","acct_pwd",18,9,0),
                new Field("구분1","qry_tp_code",9,1,0),
                new Field("단가구분","uv_tp_code",9,1,0),
                new Field("종목구분코드","stbd_tp_code",9,1,0),
                new Field("작업구분코드","job_tp_code",9,1,0),
                new Field("조회시세구분코드","qry_quot_tp_code",9,1,0),
            };
            public readonly List<Field> Single=new List<Field>{
                new Field("일자","tdy",9,2,0),
                new Field("요일(D)","tdy_day",9,2,0),
                new Field("예수금(D)","dpos",16,20,0),
                new Field("일자(D+1)","n1d",9,2,0),
                new Field("요일(D+1)","n1d_day",9,2,0),
                new Field("예수금(D+1)","d_pls_dd1_psum_dpos",16,20,0),
                new Field("일자(D+2)","n2d",9,2,0),
                new Field("요일(D+2)","n2d_day",9,2,0),
                new Field("예수금(D+2)","d_pls_dd2_psum_dpos",16,20,0),
                new Field("인출가능금액(D)","drw_aval_amt",16,20,0),
                new Field("신용금액(융자)","loan_amt",16,20,0),
                new Field("대출금액","lndo_amt",16,20,0),
                new Field("현금매수","ord_aval_cash",16,20,0),
                new Field("최대(미수)","max_ord_aval_amt",16,20,0),
                new Field("신용매수","crd_ord_aval_amt",16,20,0),
                new Field("주식매입금액","stk_buy_amt",16,20,0),
                new Field("실현손익","actl_pl_amt",16,20,0),
                new Field("채권평가금","bd_evlt_amt",16,20,0),
                new Field("주식평가금액","stk_evlt_amt",16,20,0),
                new Field("미실현손익","nactl_pl_amt",16,20,0),
                new Field("현금증거금","cash_mrgn",16,20,0),
                new Field("위탁순자산평가","trst_passt_evlt_amt",16,20,0),
                new Field("미실현손익률","nactl_pl_rt",16,20,0),
                new Field("익일반대매매","n1d_opps_trde_amt",16,20,0),
                new Field("총계약금액","cntc_amt",16,20,0),
                new Field("납입금액","pdin_amt",16,20,0),
                new Field("납입횟수","pdin_numt",9,18,0),
                new Field("채권매입금액","bd_buy_amt",16,20,0),
                new Field("상품순자산평가","gds_passt_evlt_amt",16,20,0),
                new Field("채권손익률","bd_pl_rt",16,20,0),
                new Field("대주가능금액","ls_ord_aval_amt",16,20,0),
                new Field("매입대출금액","purc_fnd_ord_aval_amt",16,20,0),
                new Field("대출만기일자","expr_ymd",9,8,0),
                new Field("세후RP금액","cma_rp_atax_evlt_amt",16,20,0),
                new Field("수시RP금액","anytm_rp_atax_evlt_amt",16,20,0),
                new Field("대주금액","ls_amt",16,20,0),
                new Field("융자_대주금액","crd_ls_amt_sumtot",16,20,0),
            };
            public readonly List<Field> Multi=new List<Field>{
                new Field("종목코드","stbd_code",9,12,11),
                new Field("종목명","stbd_nm",9,50,0),
                new Field("구분코드","qry_tp_code",9,1,0),
                new Field("구분","trde_tp_nm",9,10,0),
                new Field("잔고수량","remq_qty",16,18,0),
                new Field("전일매도미결제","p1d_sell_qty",16,18,0),
                new Field("당일매도미결제","tdy_sell_qty",16,18,0),
                new Field("전일매수미결제","p1d_buy_qty",16,18,0),
                new Field("당일매수미결제","tdy_buy_qty",16,18,0),
                new Field("결제일수량","dcd_qty",16,18,0),
                new Field("주문가능수량","ord_aval_qty",16,18,0),
                new Field("미체결수량","ncntr_qty",16,18,0),
                new Field("평균단가","buy_uv",16,20,0),
                new Field("현재가","nowv",16,20,470),
                new Field("전일대비","p1d_diff_callv",16,20,401),
                new Field("비율","uv_diff_ratio",16,20,402),
                new Field("매도호가","sell_callv",16,20,650),
                new Field("매수호가","buy_callv",16,20,702),
                new Field("평가금액","evlt_amt",16,20,0),
                new Field("미실현손익금액","nactl_pl_amt",16,20,0),
                new Field("손익률","nactl_pl_rt",16,20,0),
                new Field("보유비중","hold_ratio",16,20,0),
                new Field("융자금액","flow_amt",16,20,0),
                new Field("자기금액","self_amt",16,20,0),
                new Field("대출일","lndo_ymd",9,8,0),
                new Field("만기일","expr_ymd",9,8,0),
                new Field("신용이자","crd_int",16,20,0),
                new Field("매수일자(채권)","buy_ymd",9,8,0),
                new Field("단축코드","rdcn_code",9,12,11),
                new Field("전일종가","p1d_closv",16,20,0),
                new Field("대주손익계산구분","ls_yn",9,1,0),
                new Field("질권설정수량","pldg_estb_qty",16,18,0),
                new Field("평가잔고수량","evlt_base_qty",16,18,0),
                new Field("채권구분","bd_scrn_tp_code",9,1,0),
                new Field("매매수량단위","trde_qty_unit",16,18,0),
                new Field("매수수수료","buy_cmsn",9,20,0),
                new Field("매도수수료","sell_cmsn",9,20,0),
                new Field("거래세","trtax",9,20,0),
                new Field("분리과세구분코드","splt_txon_tp_code",9,1,0),
            };
        }
        // Generates only synthetic contract metadata. No vendor byte payload,
        // customer data, OCX or provider call is used by these tests.
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
                Action<string,int,int,uint,int> folder=(name,direction,marker,links,count)=>{
                    writer.Write((byte)1);text(name);writer.Write((byte)1);writer.Write((byte)direction);writer.Write((ushort)marker);
                    writer.Write(0);writer.Write(links);
                    if(links!=0)foreach(var link in fixture.Links) {
                        writer.Write(fixture.LinkVersion);text(link);writer.Write(0);writer.Write(fixture.KeyCount);text(fixture.KeyName);
                        writer.Write(fixture.LinkedFieldCount);foreach(var field in fixture.LinkedNames)text(field);
                        writer.Write(fixture.LinkColor);writer.Write(fixture.LinkTrailer);
                    }
                    writer.Write((ushort)count);
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
                Action<List<Field>,int,int,uint> fields=(values,direction,marker,links)=>{
                    tag("CIONodeFolder");folder("RECORD",direction,marker,links,values.Count);
                    foreach(var value in values) {
                        tag(fixture.ItemClass);writer.Write(fixture.ItemVersion);text(value.Name);writer.Write((byte)2);
                        writer.Write((byte)direction);writer.Write(0);writer.Write(value.Fid);writer.Write(value.Type);writer.Write(value.Width);
                        text(value.Alias);text(value.Default);if(fixture.ItemVersion==2)text(value.Extra);
                    }
                };
                writer.Write(fixture.Version);text(fixture.Query);text(fixture.Description);text(fixture.Service);text(fixture.Protocol);
                writer.Write(new byte[19]);writer.Write((ushort)2);
                folder("Record",1,256,0,1);fields(fixture.Input,1,fixture.InputMarker,0);
                folder("Record",fixture.OutputDirection,256,0,2);
                fields(fixture.Single,fixture.OutputDirection,fixture.SingleMarker,0);
                fields(fixture.Multi,fixture.OutputDirection,fixture.MultiMarker,fixture.LinkCount);
                writer.Flush();return stream.ToArray();
            }
        }
        static int checks;
        static void Check(bool ok,string message){checks++;if(!ok)throw new Exception("IndiHoldingsSchemaTests: "+message);}
        static void Reject(byte[] bytes,string message){var info=IndiHoldingsSchema.Inspect(bytes);Check(!info.Recognized&&info.Kind==IndiHoldingsSchemaKind.Unknown,message);}
        public static int Run() {
            checks=0;var bytes=ModernDefinition();var info=IndiHoldingsSchema.Inspect(bytes);
            Check(info.Recognized&&info.Kind==IndiHoldingsSchemaKind.Modern0208&&info.QueryName=="SABA609Q1",
                "complete synthetic expanded holdings contract is recognized without a vendor resource");
            Check(info.SerializationVersion==0x0208&&info.InputCount==8&&info.SingleOutputCount==37&&info.MultiOutputCount==39,
                "expanded holdings has distinct eight-input, thirty-seven-single and thirty-nine-multi scopes");
            Check(info.ParseOffset==bytes.Length&&info.SizeBytes==bytes.Length&&info.ParseStage=="Complete"&&info.FailureCode=="Recognized",
                "recognition consumes all fields and linked quote metadata before certifying the profile");
            Check(info.Sha256.Length==64&&info.Sha256==IndiHoldingsSchema.Inspect((byte[])bytes.Clone()).Sha256,
                "immutable holdings profile carries stable byte-specific digest evidence");
            Check(info.Diagnostic.Length<512&&!info.Diagnostic.Contains("합성"),
                "bounded profile diagnostic exposes fixed metadata rather than arbitrary definition text");
            Check(typeof(IndiHoldingsSchemaInfo).GetConstructors().Length==0
                &&typeof(IndiHoldingsSchemaInfo).GetProperties().All(property=>property.GetSetMethod()==null),
                "request-bound layout evidence has no publicly mutable constructor or property");
            Check(IndiHoldingsSchema.Inspect(Changed(x=>x.WideStrings=true)).Recognized,
                "Unicode MFC framing preserves the exact expanded field profile");
            var description=IndiHoldingsSchema.Inspect(Changed(x=>x.Description="다른 합성 설명"));
            Check(description.Recognized&&description.Sha256!=info.Sha256,
                "nonsemantic description changes retain field recognition but not the original provenance hash");
            Reject(Changed(x=>x.Version=0x0207),"older envelopes cannot certify the reviewed expanded holdings layout");
            Reject(Changed(x=>x.Query="SABA231Q1"),"another account query cannot supply holdings quantity-layout evidence");
            Reject(Changed(x=>x.Service="113"),"holdings proof is restricted to account-read service112");
            Reject(Changed(x=>x.Protocol="Q1"),"another request protocol cannot retain the holdings profile");
            Reject(Changed(x=>x.ItemVersion=2),"unreviewed item framing cannot shift quantity field boundaries");
            Reject(Changed(x=>x.ClassVersion=2),"unknown serialized runtime class versions are not accepted");
            Reject(Changed(x=>x.ItemClass="OtherNode"),"unknown item class cannot supply a field by substring");
            Reject(Changed(x=>x.InputMarker=257),"request scope cannot be reclassified as multi output");
            Reject(Changed(x=>x.SingleMarker=257),"single and multi output records are not interchangeable");
            Reject(Changed(x=>x.MultiMarker=256),"current quantity proof requires the repeating output marker");
            Reject(Changed(x=>x.OutputDirection=1),"output cannot be mistaken for the input direction");
            Reject(Changed(x=>x.Input.RemoveAt(7)),"all installed inputs including quotation source are required");
            Reject(Changed(x=>x.Single.RemoveAt(36)),"truncated account aggregate metadata is not a full schema");
            Reject(Changed(x=>x.Multi.RemoveAt(38)),"the full expanded repeating output must exist");
            Reject(Changed(x=>x.Multi.Add(new Field("future","future",9,1,0))),"future field counts need a reviewed profile");
            for(int index=0;index<8;index++) {
                int field=index;Reject(Changed(x=>x.Input[field].Alias="wrong"),"every request input alias retains its exact index");
            }
            foreach(int index in new[]{0,1,2,3,4,5,6,7,8,9,10,11,12,13,18,19,20,22,32,38}) {
                int field=index;Reject(Changed(x=>x.Multi[field].Alias="wrong"),"consumed quantity, kind, valuation and source fields are bound by exact alias");
            }
            foreach(int index in new[]{15,18,19,22}) {
                int field=index;Reject(Changed(x=>x.Single[field].Alias="wrong"),"direct aggregate valuation fields retain their original indices");
            }
            Reject(Changed(x=>x.Multi[32].Name="잔고수량"),"evaluation quantity label cannot silently become the legacy balance label");
            Reject(Changed(x=>x.Multi[32].Width=17),"evaluation quantity width is part of the recognized contract");
            Reject(Changed(x=>x.Multi[32].Type=9),"evaluation quantity scalar type cannot change");
            Reject(Changed(x=>x.Input[0].Fid=0),"the account input field binding cannot be removed");
            Reject(Changed(x=>{var field=x.Multi[4];x.Multi[4]=x.Multi[32];x.Multi[32]=field;}),
                "legacy balance and evaluation quantity positions cannot be swapped");
            Reject(Changed(x=>x.LinkCount=0),"the current repeating-record quote linkage framing is required");
            Reject(Changed(x=>x.LinkCount=1),"mismatched linked-record counts cannot leave a recognized profile");
            Reject(Changed(x=>x.Links[0]="XX"),"unknown realtime linkage cannot replace SC");
            Reject(Changed(x=>x.Links[1]="XX"),"unknown realtime linkage cannot replace WC");
            Reject(Changed(x=>x.LinkVersion=2),"unknown realtime linkage versions fail closed");
            Reject(Changed(x=>x.KeyCount=2),"linked symbol key counts are bounded and checked");
            Reject(Changed(x=>x.KeyName="other"),"linked symbol key identity is verified");
            Reject(Changed(x=>x.LinkedFieldCount=5),"declared linked price field counts cannot shift the remaining parse");
            Reject(Changed(x=>x.LinkedNames[5]="other"),"linked current-price semantics are verified");
            Reject(Changed(x=>x.LinkColor=0),"unreviewed link metadata cannot replace the known framing");
            Reject(Changed(x=>x.LinkTrailer=1),"the reviewed linkage trailer must match");
            Reject(bytes.Concat(new byte[]{0}).ToArray(),"bytes after the last output cannot retain a recognized profile");
            bool allPrefixesFail=true;
            for(int length=0;length<bytes.Length;length++)
                if(IndiHoldingsSchema.Inspect(bytes.Take(length).ToArray()).Recognized){allPrefixesFail=false;break;}
            Check(allPrefixesFail,"every truncated prefix remains unrecognized without leaking a parser exception");
            Reject(null,"missing definitions cannot establish current-quantity semantics");
            Reject(new byte[1024*1024+1],"the resource byte count has an explicit upper bound");
            var malformed=(byte[])bytes.Clone();malformed[2]=255;malformed[3]=255;malformed[4]=255;
            Reject(malformed,"malformed MFC string lengths cannot escape the parser bounds");
            info=IndiHoldingsSchema.Inspect(Changed(x=>{x.Description="SECRET_ACCOUNT_12345678901 PASSWORD_4321";x.Multi[32].Alias="SECRET_ACCOUNT_12345678901";}));
            Check(!info.Recognized&&!info.Diagnostic.Contains("SECRET")&&!info.Diagnostic.Contains("12345678901")&&!info.Diagnostic.Contains("PASSWORD"),
                "provider definition text and exception contents are never copied to profile diagnostics");
            var directory=Path.Combine(Path.GetTempPath(),"Indi609-SECRET_ACCOUNT-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory,"qry"));
            try {
                var starter=Path.Combine(directory,IndiInstallation.StarterFileName);File.WriteAllText(starter,"synthetic placeholder; never executed");
                File.WriteAllBytes(Path.Combine(directory,"qry","saba609q1.xtr"),bytes);
                info=IndiHoldingsSchema.ReadInstalled(starter);
                Check(info.Recognized&&info.Sha256==IndiHoldingsSchema.Inspect(bytes).Sha256,
                    "installed profile reads only the fixed query below the validated starter");
                Check(!info.Diagnostic.Contains(directory)&&!info.Diagnostic.Contains("SECRET_ACCOUNT"),
                    "filesystem and account-like path text do not enter fixed diagnostics");
                Check(!IndiHoldingsSchema.ReadInstalled(Path.Combine(directory,"other.exe")).Recognized,
                    "an arbitrary executable is not trusted as a provider installation");
                File.WriteAllBytes(Path.Combine(directory,"qry","saba609q1.xtr"),bytes.Concat(new byte[]{0}).ToArray());
                Check(!IndiHoldingsSchema.ReadInstalled(starter).Recognized,"installed malformed metadata does not supply the quantity profile");
                File.Delete(starter);
                Check(!IndiHoldingsSchema.ReadInstalled(starter).Recognized,"a missing starter cannot resolve accepted profile evidence");
            }finally{Directory.Delete(directory,true);}
            Check(!IndiHoldingsSchema.ReadInstalled("giexpertstarter.exe").Recognized
                &&!IndiHoldingsSchema.ReadInstalled(null).Recognized
                &&!IndiHoldingsSchema.ReadInstalled("bad\0path").Recognized,
                "relative, null and invalid starter paths fail without exposing system text");
            return checks;
        }
    }
}
