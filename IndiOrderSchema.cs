using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShinhanNativeTrader {
    public enum IndiOrderSchemaKind { Unknown,Legacy,Modern }

    public sealed class IndiOrderSchemaInfo {
        public IndiOrderSchemaKind Kind {get;internal set;}
        public string QueryName {get;internal set;}
        public int SizeBytes {get;internal set;}
        public string FailureCode {get;internal set;}
        public string ParseStage {get;internal set;}
        public int ParseOffset {get;internal set;}
        public string HelperSource {get;internal set;}
        public int SerializationVersion {get;internal set;}
        public string RouteSource {get;internal set;}
        public int InputCount {get;internal set;}
        public int OutputCount {get;internal set;}
        public string Sha256 {get;internal set;}
        public int? KrxRouteInputIndex {get;internal set;}
        public string KrxRouteCode {get;internal set;}
        public string Diagnostic {get;internal set;}
    }

    // Reads query definitions only. No vendor code is loaded or executed and
    // neither local paths nor arbitrary resource strings enter diagnostics.
    public static class IndiOrderSchema {
        const int MaximumLength=1024*1024;
        static readonly Encoding Ansi=Encoding.GetEncoding(949,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
        static readonly Encoding Unicode=new UnicodeEncoding(false,false,true);
        static string Canonical(string value){return (value??"").Replace(" ","").Replace("메시지","메세지");}
        static bool Query(string value){return value=="SABA101U1"||value=="SABA102U1";}
        static IndiOrderSchemaInfo Unknown(string query,string code,string stage,int size=0) {
            var result=new IndiOrderSchemaInfo{Kind=IndiOrderSchemaKind.Unknown,QueryName=Query(query)?query:"Unknown",SizeBytes=size,
                FailureCode=code,ParseStage=stage,HelperSource="Absent",RouteSource="Absent"};Describe(result);return result;
        }
        static void Describe(IndiOrderSchemaInfo result) {
            result.Diagnostic="Order schema: "+result.QueryName+"; status="+result.FailureCode+"; kind="+result.Kind
                +"; bytes="+result.SizeBytes.ToString(CultureInfo.InvariantCulture)+"; SHA256="+(result.Sha256??"Unavailable")
                +"; stage="+result.ParseStage+"; offset="+result.ParseOffset.ToString(CultureInfo.InvariantCulture)
                +"; format="+result.SerializationVersion.ToString("X4",CultureInfo.InvariantCulture)
                +"; inputs="+result.InputCount.ToString(CultureInfo.InvariantCulture)+"; outputs="+result.OutputCount.ToString(CultureInfo.InvariantCulture)
                +"; helper="+result.HelperSource+"; routeSource="+result.RouteSource+"; KRX route="+(result.KrxRouteCode==null?"Unverified":"Verified");
        }

        public static IndiOrderSchemaInfo ReadInstalled(string starterPath,string queryName) {
            try {
                if(!Query(queryName)||string.IsNullOrWhiteSpace(starterPath)||!Path.IsPathRooted(starterPath)
                    ||starterPath.IndexOf('"')>=0||starterPath.Any(char.IsControl))return Unknown(queryName,"Unavailable","Starter");
                string starter=Path.GetFullPath(starterPath);
                if(!string.Equals(Path.GetFileName(starter),IndiInstallation.StarterFileName,StringComparison.OrdinalIgnoreCase)
                    ||!File.Exists(starter))return Unknown(queryName,"Unavailable","Starter");
                string root=Path.GetDirectoryName(starter);
                var data=ReadBounded(Path.Combine(root,"qry",queryName.ToLowerInvariant()+".xtr"));
                byte[] primary=OptionalRead(Path.Combine(root,"qry","config","helpertrinfo.dat"));
                byte[] legacy=OptionalRead(Path.Combine(root,"config","helpertrinfo.dat"));
                string peerQuery=queryName=="SABA101U1"?"SABA102U1":"SABA101U1";
                byte[] peer=OptionalRead(Path.Combine(root,"qry",peerQuery.ToLowerInvariant()+".xtr"));
                var result=Inspect(data,queryName,primary??legacy,peer);
                result.HelperSource=primary!=null?"Primary":legacy!=null?"Legacy":"Absent";
                if(primary!=null&&legacy!=null) {
                    var legacyResult=Inspect(data,queryName,legacy,peer);
                    if(result.RouteSource=="Conflict"||legacyResult.RouteSource=="Conflict"
                        ||result.KrxRouteCode!=null&&legacyResult.KrxRouteCode!=null&&
                        (result.KrxRouteInputIndex!=legacyResult.KrxRouteInputIndex||result.KrxRouteCode!=legacyResult.KrxRouteCode)) {
                        result.KrxRouteInputIndex=null;result.KrxRouteCode=null;result.HelperSource="Conflict";result.RouteSource="Conflict";
                    }
                }
                Describe(result);return result;
            }catch{return Unknown(queryName,"Unavailable","ReadDefinition");}
        }

        static byte[] OptionalRead(string path){try{return ReadBounded(path);}catch{return null;}}

        static byte[] ReadBounded(string path) {
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(input.Length<=0||input.Length>MaximumLength)throw new InvalidDataException();
                var data=new byte[(int)input.Length];int offset=0;
                while(offset<data.Length){int count=input.Read(data,offset,data.Length-offset);if(count<=0)throw new InvalidDataException();offset+=count;}
                if(input.ReadByte()!=-1)throw new InvalidDataException();return data;
            }
        }

        sealed class DefinitionField {
            public string Name,Alias;
            public byte Type;
            public uint Width;
        }
        sealed class ParsedDefinition {
            public IndiOrderSchemaInfo Info;
            public List<DefinitionField> Input;
        }
        public static IndiOrderSchemaInfo Inspect(byte[] bytes,string queryName,byte[] helperBytes=null,byte[] peerDefinitionBytes=null) {
            var parsed=ParseDefinition(bytes,queryName,helperBytes);var result=parsed.Info;
            if(result.Kind!=IndiOrderSchemaKind.Modern||parsed.Input==null)return result;
            int index=queryName=="SABA101U1"?37:35;
            var own=ReadRouteMapping(parsed.Input,index,queryName,helperBytes);
            if(own.Code!=null)SetRoute(result,index,own.Code,"OwnHelper");
            else if(own.Invalid)result.RouteSource="Unverified";
            // The installed 0x0208 new/amend resources identify the same enum
            // through label, internal alias, scalar type and width. Only that
            // structural match permits the peer Helper's explicit mapping.
            if(peerDefinitionBytes!=null&&result.SerializationVersion==0x0208) {
                string peerQuery=queryName=="SABA101U1"?"SABA102U1":"SABA101U1";
                var peer=ParseDefinition(peerDefinitionBytes,peerQuery,helperBytes);
                int peerIndex=peerQuery=="SABA101U1"?37:35;
                if(peer.Info.Kind==IndiOrderSchemaKind.Modern&&peer.Info.SerializationVersion==0x0208&&peer.Input!=null
                    &&SameRouteDomain(parsed.Input[index],peer.Input[peerIndex])) {
                    var route=ReadRouteMapping(peer.Input,peerIndex,peerQuery,helperBytes);
                    if(own.Code!=null&&route.Code!=null&&own.Code!=route.Code) {
                        result.KrxRouteCode=null;result.KrxRouteInputIndex=null;result.RouteSource="Conflict";
                    }else if(own.Code==null&&!own.Invalid&&route.Code!=null)
                        SetRoute(result,index,route.Code,"PeerMatched");
                }
            }
            Describe(result);return result;
        }
        static ParsedDefinition ParseDefinition(byte[] bytes,string queryName,byte[] helperBytes) {
            if(!Query(queryName)||bytes==null||bytes.Length==0||bytes.Length>MaximumLength)
                return new ParsedDefinition{Info=Unknown(queryName,"UnsupportedFormat","Bounds",bytes==null?0:bytes.Length)};
            string hash;
            using(var algorithm=SHA256.Create())hash=BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            var result=Unknown(queryName,"UnsupportedFormat","Header",bytes.Length);result.Sha256=hash;
            result.HelperSource=helperBytes==null?"Absent":"Provided";
            var reader=new DefinitionReader(bytes);List<DefinitionField> input=null;
            try {
                result.SerializationVersion=reader.Word();
                if((result.SerializationVersion!=0x0207&&result.SerializationVersion!=0x0208)||reader.String()!=queryName)throw new InvalidDataException();
                result.ParseStage="Identity";
                reader.String();if(reader.String()!="113"||reader.String()!="Q2")throw new InvalidDataException();
                result.ParseStage="RecordHeader";
                reader.Skip(19);if(reader.Word()!=2)throw new InvalidDataException();
                result.ParseStage="InputRecord";input=reader.Record(1);result.InputCount=input.Count;
                result.ParseStage="OutputRecord";var output=reader.Record(2);result.OutputCount=output.Count;
                result.ParseStage="Trailing";if(!reader.End)throw new InvalidDataException();
                int legacyInputs=queryName=="SABA101U1"?36:35;
                result.ParseStage="InputSemantics";
                if(!InputsMatch(input))throw new InvalidDataException();
                result.ParseStage="OutputSemantics";
                bool legacy=input.Count==legacyInputs&&Matches(output,new[]{"주문번호","ORC주문번호","메세지구분","메세지1","메세지2","메세지3"});
                bool modern=(result.SerializationVersion==0x0207&&input.Count==legacyInputs+2
                    ||result.SerializationVersion==0x0208&&input.Count==legacyInputs+3&&ModernInputTail(input,queryName))&&ModernOutput(output);
                result.Kind=legacy?IndiOrderSchemaKind.Legacy:modern?IndiOrderSchemaKind.Modern:IndiOrderSchemaKind.Unknown;
                result.FailureCode=result.Kind==IndiOrderSchemaKind.Unknown?"UnsupportedSchema":"Recognized";
                if(result.Kind!=IndiOrderSchemaKind.Unknown)result.ParseStage="Complete";
            }catch{}
            result.ParseOffset=reader.Position;Describe(result);return new ParsedDefinition{Info=result,Input=input};
        }

        static bool InputsMatch(IList<DefinitionField> fields) {
            if(fields.Count<35)return false;
            var required=new Dictionary<int,string[]>{{0,new[]{"계좌번호","종합계좌번호"}},{1,new[]{"상품","상품구분"}},{2,new[]{"비밀번호"}},
                {5,new[]{"선물대용매도구분"}},{6,new[]{"신용거래구분"}},{7,new[]{"매도매수구분"}},{8,new[]{"종목코드"}},
                {9,new[]{"주문수량"}},{10,new[]{"주문가격"}},{11,new[]{"정규시간외구분코드"}},{12,new[]{"호가유형코드"}},{16,new[]{"원주문번호"}}};
            return required.All(pair=>pair.Value.Contains(Canonical(fields[pair.Key].Name),StringComparer.Ordinal));
        }
        static bool Matches(IList<DefinitionField> actual,string[] expected) {
            return actual.Count==expected.Length&&actual.Select(value=>Canonical(value.Name)).SequenceEqual(expected.Select(Canonical),StringComparer.Ordinal);
        }
        static bool ModernInputTail(IList<DefinitionField> input,string query) {
            int route=query=="SABA101U1"?37:35,condition=36,original=query=="SABA101U1"?38:37;
            return Canonical(input[condition].Name)=="주문조건가격"&&Canonical(input[original].Name)=="SOR원주문번호"
                &&input[condition].Alias=="ord_cond_price"&&input[condition].Type==9&&input[condition].Width==20
                &&input[original].Alias=="sor_orig_ord_no"&&input[original].Type==9&&input[original].Width==18
                &&Canonical(input[route].Name)=="주문방식구분코드"&&input[route].Alias=="ord_meth_tp_code"
                &&input[route].Type==9&&input[route].Width==1;
        }
        static bool ModernOutput(IList<DefinitionField> actual) {
            if(actual.Count!=8)return false;
            return new[]{"SOR","KRX","NXT","ORC"}.Select((venue,index)=> {
                string value=Canonical(actual[index].Name);return value==venue+"주문번호"||value=="주문번호("+venue+")";
            }).All(value=>value)&&Matches(actual.Skip(4).ToList(),new[]{"메세지구분","메세지1","메세지2","메세지3"});
        }
        sealed class RouteMapping {public string Code;public bool Invalid;}
        static void SetRoute(IndiOrderSchemaInfo result,int index,string code,string source) {
            result.KrxRouteInputIndex=index;result.KrxRouteCode=code;result.RouteSource=source;
        }
        static bool SameRouteDomain(DefinitionField first,DefinitionField second) {
            return Canonical(first.Name)=="주문방식구분코드"&&Canonical(second.Name)==Canonical(first.Name)
                &&first.Alias=="ord_meth_tp_code"&&second.Alias==first.Alias&&first.Type==9&&second.Type==9&&first.Width==1&&second.Width==1;
        }
        static RouteMapping ReadRouteMapping(IList<DefinitionField> input,int index,string query,byte[] helperBytes) {
            var result=new RouteMapping();
            if(index>=input.Count||helperBytes==null||helperBytes.Length==0||helperBytes.Length>MaximumLength)return result;
            string label=Canonical(input[index].Name);
            if(label!="주문방식"&&label!="주문방식코드"&&label!="주문방식구분코드"&&label!="주문전송방식"&&label!="주문거래소구분"&&label!="거래소구분")return result;
            string text;
            try{text=helperBytes.Length>=2&&helperBytes[0]==255&&helperBytes[1]==254?Unicode.GetString(helperBytes,2,helperBytes.Length-2):Ansi.GetString(helperBytes);}catch{return result;}
            string[] lines=text.Replace("\r","").Split('\n');bool active=false;int sections=0,rows=0;var candidates=new List<string>();bool hasDescription=false;
            foreach(string line in lines) {
                if(line.StartsWith("*START,",StringComparison.Ordinal)) {
                    var fields=line.Split(',');active=fields.Length>1&&fields[1]==query;if(active)sections++;continue;
                }
                if(line.StartsWith("*END",StringComparison.Ordinal)){active=false;continue;}
                if(!active)continue;
                var columns=line.Split(',');int field;
                if(columns.Length<4||columns[0]!="SI"||!int.TryParse(columns[1],NumberStyles.None,CultureInfo.InvariantCulture,out field)||field!=index)continue;
                rows++;
                if(Canonical(columns[2])!=label||columns[3].Length>4096){result.Invalid=true;continue;}
                hasDescription|=!string.IsNullOrWhiteSpace(columns[3]);
                foreach(Match match in Regex.Matches(columns[3],@"(?:^|[\^\s])([0-9]{1,2}):[ \t]*KRX(?:[ \t]*주문)?(?=[ \t]*(?:\^|$|[0-9]{1,2}:))",RegexOptions.CultureInvariant))candidates.Add(match.Groups[1].Value);
            }
            result.Invalid|=sections!=1||rows>1||candidates.Count>1||hasDescription&&candidates.Count==0
                ||candidates.Any(code=>code.Length>input[index].Width);
            if(!result.Invalid&&candidates.Count==1&&candidates[0].Length<=input[index].Width)result.Code=candidates[0];
            return result;
        }

        sealed class DefinitionReader {
            readonly byte[] data;int position,nextClassIndex=1;
            readonly Dictionary<int,string> classes=new Dictionary<int,string>();
            public DefinitionReader(byte[] data){this.data=data;}
            public int Position {get{return position;}}
            public bool End {get{return position==data.Length;}}
            public void Skip(int count){if(count<0||position>data.Length-count)throw new InvalidDataException();position+=count;}
            byte Byte(){if(position>=data.Length)throw new InvalidDataException();return data[position++];}
            public ushort Word(){return (ushort)(Byte()|(Byte()<<8));}
            uint Dword(){return (uint)(Word()|((uint)Word()<<16));}
            int Length(out bool wide) {
                wide=false;int count=Byte();if(count<255)return count;
                count=Word();if(count==65534){wide=true;return LengthAfterUnicode();}
                return count<65535?count:CheckedLength(Dword());
            }
            int LengthAfterUnicode(){int count=Byte();if(count<255)return count;count=Word();return count<65535?count:CheckedLength(Dword());}
            static int CheckedLength(uint count){if(count>1024)throw new InvalidDataException();return (int)count;}
            public string String() {
                bool wide;int count=Length(out wide);if(count>1024)throw new InvalidDataException();int bytes=wide?checked(count*2):count;
                int start=position;Skip(bytes);return (wide?Unicode:Ansi).GetString(data,start,bytes);
            }
            string Class() {
                ushort tag=Word();string name;
                if(tag==65535) {
                    if(Word()!=1)throw new InvalidDataException();int length=Word();if(length<=0||length>40)throw new InvalidDataException();
                    int start=position;Skip(length);name=Encoding.ASCII.GetString(data,start,length);classes.Add(nextClassIndex++,name);
                }else if((tag&32768)==0||!classes.TryGetValue(tag&32767,out name))throw new InvalidDataException();
                nextClassIndex++;return name;
            }
            int Folder(string name,int direction) {
                if(Byte()!=1||String()!=name||Byte()!=1||Byte()!=direction||Word()!=256||Dword()!=0||Dword()!=0)throw new InvalidDataException();
                int count=Word();if(count>100)throw new InvalidDataException();return count;
            }
            public List<DefinitionField> Record(int direction) {
                if(Folder("Record",direction)!=1||Class()!="CIONodeFolder")throw new InvalidDataException();
                int count=Folder("RECORD",direction);var result=new List<DefinitionField>();
                for(int index=0;index<count;index++) {
                    if(Class()!="CIONodeItem"||Byte()!=1)throw new InvalidDataException();string label=String();
                    if(Byte()!=2||Byte()!=direction)throw new InvalidDataException();Dword();Dword();byte type=Byte();uint width=Dword();
                    if(type!=9&&type!=16&&type!=18||width==0||width>4096)throw new InvalidDataException();string alias=String();String();result.Add(new DefinitionField{Name=label,Alias=alias,Type=type,Width=width});
                }
                return result;
            }
        }
    }
}
