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
        static IndiOrderSchemaInfo Unknown(string code){return new IndiOrderSchemaInfo{Kind=IndiOrderSchemaKind.Unknown,Diagnostic="Order schema: "+code};}

        public static IndiOrderSchemaInfo ReadInstalled(string starterPath,string queryName) {
            try {
                if(!Query(queryName)||string.IsNullOrWhiteSpace(starterPath)||!Path.IsPathRooted(starterPath)
                    ||starterPath.IndexOf('"')>=0||starterPath.Any(char.IsControl))return Unknown("Unavailable");
                string starter=Path.GetFullPath(starterPath);
                if(!string.Equals(Path.GetFileName(starter),IndiInstallation.StarterFileName,StringComparison.OrdinalIgnoreCase)
                    ||!File.Exists(starter))return Unknown("Unavailable");
                string root=Path.GetDirectoryName(starter);
                var data=ReadBounded(Path.Combine(root,"qry",queryName.ToLowerInvariant()+".xtr"));
                byte[] helper=null;
                try{helper=ReadBounded(Path.Combine(root,"config","helpertrinfo.dat"));}catch{}
                return Inspect(data,queryName,helper);
            }catch{return Unknown("Unavailable");}
        }

        static byte[] ReadBounded(string path) {
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(input.Length<=0||input.Length>MaximumLength)throw new InvalidDataException();
                var data=new byte[(int)input.Length];int offset=0;
                while(offset<data.Length){int count=input.Read(data,offset,data.Length-offset);if(count<=0)throw new InvalidDataException();offset+=count;}
                if(input.ReadByte()!=-1)throw new InvalidDataException();return data;
            }
        }

        public static IndiOrderSchemaInfo Inspect(byte[] bytes,string queryName,byte[] helperBytes=null) {
            if(!Query(queryName)||bytes==null||bytes.Length==0||bytes.Length>MaximumLength)return Unknown("UnsupportedFormat");
            string hash;
            using(var algorithm=SHA256.Create())hash=BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            var result=Unknown("UnsupportedFormat");result.Sha256=hash;
            try {
                var reader=new DefinitionReader(bytes);
                if(reader.Word()!=0x0207||reader.String()!=queryName)throw new InvalidDataException();
                reader.String();if(reader.String()!="113"||reader.String()!="Q2")throw new InvalidDataException();
                reader.Skip(19);if(reader.Word()!=2)throw new InvalidDataException();
                var input=reader.Record(1);var output=reader.Record(2);if(!reader.End)throw new InvalidDataException();
                result.InputCount=input.Count;result.OutputCount=output.Count;
                int legacyInputs=queryName=="SABA101U1"?36:35;
                if(!InputsMatch(input))throw new InvalidDataException();
                bool legacy=input.Count==legacyInputs&&Matches(output,new[]{"주문번호","ORC주문번호","메세지구분","메세지1","메세지2","메세지3"});
                bool modern=input.Count==legacyInputs+2&&ModernOutput(output);
                result.Kind=legacy?IndiOrderSchemaKind.Legacy:modern?IndiOrderSchemaKind.Modern:IndiOrderSchemaKind.Unknown;
                if(modern)ReadRoute(result,input,queryName,helperBytes);
                result.Diagnostic="Order schema: "+queryName+"; kind="+result.Kind+"; inputs="+result.InputCount.ToString(CultureInfo.InvariantCulture)
                    +"; outputs="+result.OutputCount.ToString(CultureInfo.InvariantCulture)+"; SHA256="+hash
                    +"; KRX route="+(result.KrxRouteCode==null?"Unverified":"Verified");
                return result;
            }catch{return result;}
        }

        static bool InputsMatch(IList<string> names) {
            if(names.Count<35)return false;
            var required=new Dictionary<int,string[]>{{0,new[]{"계좌번호","종합계좌번호"}},{1,new[]{"상품","상품구분"}},{2,new[]{"비밀번호"}},
                {5,new[]{"선물대용매도구분"}},{6,new[]{"신용거래구분"}},{7,new[]{"매도매수구분"}},{8,new[]{"종목코드"}},
                {9,new[]{"주문수량"}},{10,new[]{"주문가격"}},{11,new[]{"정규시간외구분코드"}},{12,new[]{"호가유형코드"}},{16,new[]{"원주문번호"}}};
            return required.All(pair=>pair.Value.Contains(Canonical(names[pair.Key]),StringComparer.Ordinal));
        }
        static bool Matches(IList<string> actual,string[] expected) {
            return actual.Count==expected.Length&&actual.Select(Canonical).SequenceEqual(expected.Select(Canonical),StringComparer.Ordinal);
        }
        static bool ModernOutput(IList<string> actual) {
            if(actual.Count!=8)return false;
            return new[]{"SOR","KRX","NXT","ORC"}.Select((venue,index)=> {
                string value=Canonical(actual[index]);return value==venue+"주문번호"||value=="주문번호("+venue+")";
            }).All(value=>value)&&Matches(actual.Skip(4).ToList(),new[]{"메세지구분","메세지1","메세지2","메세지3"});
        }

        static void ReadRoute(IndiOrderSchemaInfo result,IList<string> input,string query,byte[] helperBytes) {
            int index=query=="SABA101U1"?37:35;
            if(index>=input.Count||helperBytes==null||helperBytes.Length==0||helperBytes.Length>MaximumLength)return;
            string label=Canonical(input[index]);
            if(label!="주문방식"&&label!="주문방식코드"&&label!="주문전송방식"&&label!="주문거래소구분"&&label!="거래소구분")return;
            string text;
            try{text=helperBytes.Length>=2&&helperBytes[0]==255&&helperBytes[1]==254?Unicode.GetString(helperBytes,2,helperBytes.Length-2):Ansi.GetString(helperBytes);}catch{return;}
            string[] lines=text.Replace("\r","").Split('\n');bool active=false;int sections=0;var candidates=new List<string>();
            foreach(string line in lines) {
                if(line.StartsWith("*START,",StringComparison.Ordinal)) {
                    var fields=line.Split(',');active=fields.Length>1&&fields[1]==query;if(active)sections++;continue;
                }
                if(line.StartsWith("*END",StringComparison.Ordinal)){active=false;continue;}
                if(!active)continue;
                var columns=line.Split(',');int field;
                if(columns.Length<4||columns[0]!="SI"||!int.TryParse(columns[1],NumberStyles.None,CultureInfo.InvariantCulture,out field)||field!=index)continue;
                if(Canonical(columns[2])!=label||columns[3].Length>4096)continue;
                foreach(Match match in Regex.Matches(columns[3],@"(?:^|\^)([0-9]{1,2}):\s*KRX(?:\s*주문)?\s*(?=\^|$)",RegexOptions.CultureInvariant))candidates.Add(match.Groups[1].Value);
            }
            if(sections==1&&candidates.Count==1){result.KrxRouteInputIndex=index;result.KrxRouteCode=candidates[0];}
        }

        sealed class DefinitionReader {
            readonly byte[] data;int position,nextClassIndex=1;
            readonly Dictionary<int,string> classes=new Dictionary<int,string>();
            public DefinitionReader(byte[] data){this.data=data;}
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
            public List<string> Record(int direction) {
                if(Folder("Record",direction)!=1||Class()!="CIONodeFolder")throw new InvalidDataException();
                int count=Folder("RECORD",direction);var result=new List<string>();
                for(int index=0;index<count;index++) {
                    if(Class()!="CIONodeItem"||Byte()!=1)throw new InvalidDataException();string label=String();
                    if(Byte()!=2||Byte()!=direction)throw new InvalidDataException();Dword();Dword();byte type=Byte();uint width=Dword();
                    if(type!=9&&type!=16&&type!=18||width==0||width>4096)throw new InvalidDataException();String();String();result.Add(label);
                }
                return result;
            }
        }
    }
}
