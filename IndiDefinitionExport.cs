using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace ShinhanNativeTrader {
    // User-invoked export of static vendor definitions only. It never opens an
    // OCX, sends a request, or reads account/settings/log/journal files.
    public static class IndiDefinitionExport {
        const int MaximumFileLength=1024*1024;
        sealed class Definition {
            public string Name,Status;
            public byte[] Bytes;
            public long? Length;
        }
        static bool SafeAbsolutePath(string path) {
            return !string.IsNullOrWhiteSpace(path)&&Path.IsPathRooted(path)
                &&path.IndexOf('"')<0&&!path.Any(char.IsControl);
        }
        static string StarterDirectory(string starterPath) {
            try {
                if(!SafeAbsolutePath(starterPath))throw new ArgumentException();
                var full=Path.GetFullPath(starterPath);
                if(!string.Equals(Path.GetFileName(full),IndiInstallation.StarterFileName,StringComparison.OrdinalIgnoreCase)
                    ||!File.Exists(full))throw new ArgumentException();
                return Path.GetDirectoryName(full);
            }catch{throw new ArgumentException("An existing registered Indi starter path is required.","starterPath");}
        }
        static Definition Read(string root,string relative) {
            var item=new Definition{Name=relative,Status="Unavailable"};
            try {
                string path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                if(!File.Exists(path)){item.Status="Missing";return item;}
                using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    item.Length=input.Length;
                    if(input.Length==0){item.Status="Empty";return item;}
                    if(input.Length>MaximumFileLength){item.Status="TooLarge";return item;}
                    var bytes=new byte[(int)input.Length];int offset=0;
                    while(offset<bytes.Length) {
                        int count=input.Read(bytes,offset,bytes.Length-offset);
                        if(count<=0)return item;offset+=count;
                    }
                    if(input.ReadByte()!=-1)return item;
                    item.Bytes=bytes;item.Status="Included";return item;
                }
            }catch{return item;}
        }
        static string Digest(byte[] value) {
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(value)).Replace("-","").ToLowerInvariant();
        }
        public static string Save(string starterPath,string destinationDirectory) {
            var root=StarterDirectory(starterPath);
            string destination;
            try {
                if(!SafeAbsolutePath(destinationDirectory))throw new ArgumentException();
                destination=Path.GetFullPath(destinationDirectory);
            }catch{throw new ArgumentException("An absolute export directory is required.","destinationDirectory");}
            var items=new List<Definition>();
            foreach(var name in new[]{"saba609q1.xtr","saba101u1.xtr","saba102u1.xtr","saba231q1.xtr"})items.Add(Read(root,"qry/"+name));
            if(!items.Any(item=>item.Status=="Included"))throw new InvalidOperationException("No readable static Indi query definition was found.");
            var helper=Read(root,"qry/config/helpertrinfo.dat");
            if(helper.Status!="Included")helper=Read(root,"config/helpertrinfo.dat");
            items.Add(helper);
            string path=null,temporary=null;
            try {
                Directory.CreateDirectory(destination);
                path=Path.Combine(destination,"indi-query-definitions-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".xml");
                temporary=path+".tmp";
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    var settings=new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true,CloseOutput=false};
                    using(var writer=XmlWriter.Create(output,settings)) {
                        writer.WriteStartDocument();writer.WriteStartElement("IndiQueryDefinitions");writer.WriteAttributeString("version","1");
                        foreach(var item in items) {
                            writer.WriteStartElement("File");writer.WriteAttributeString("name",item.Name);writer.WriteAttributeString("status",item.Status);
                            if(item.Length.HasValue)writer.WriteAttributeString("bytes",item.Length.Value.ToString(CultureInfo.InvariantCulture));
                            if(item.Bytes!=null) {
                                writer.WriteAttributeString("sha256",Digest(item.Bytes));writer.WriteAttributeString("encoding","base64");
                                writer.WriteBase64(item.Bytes,0,item.Bytes.Length);
                            }
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();writer.WriteEndDocument();
                    }
                    output.Flush(true);
                }
                // The temporary file shares its destination volume. Move never
                // replaces an existing export and publishes only complete XML.
                File.Move(temporary,path);temporary=null;return path;
            }catch{throw new InvalidOperationException("Static Indi query definition export could not be saved.");}
            finally{if(temporary!=null)try{File.Delete(temporary);}catch{}}
        }
    }
}
