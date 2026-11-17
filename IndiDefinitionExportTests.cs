using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace ShinhanNativeTrader {
    public static class IndiDefinitionExportTests {
        static int checks;
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("IndiDefinitionExportTests: "+name);}
        static void Reject(Action action,string name) {
            Exception failure=null;try{action();}catch(Exception error){failure=error;}
            Check(failure is ArgumentException||failure is InvalidOperationException,name);
        }
        static XmlElement Entry(XmlDocument document,string name) {
            return document.DocumentElement.ChildNodes.Cast<XmlElement>().Single(item=>item.GetAttribute("name")==name);
        }
        public static int Run() {
            checks=0;var root=Path.Combine(Path.GetTempPath(),"ShinhanDefinitionExport-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                string starter=Path.Combine(root,IndiInstallation.StarterFileName),destination=Path.Combine(root,"exports");
                string qry=Path.Combine(root,"qry"),modern=Path.Combine(qry,"config"),legacy=Path.Combine(root,"config");
                File.WriteAllBytes(starter,new byte[0]);Directory.CreateDirectory(modern);Directory.CreateDirectory(legacy);
                Reject(()=>IndiDefinitionExport.Save(starter,destination),"an empty query directory cannot produce a misleading successful export");
                Check(!Directory.Exists(destination),"missing definitions create no destination or partial output");
                byte[] body={97,98,99};File.WriteAllBytes(Path.Combine(qry,"saba609q1.xtr"),body);
                File.WriteAllText(Path.Combine(qry,"saba101u1.xtr"),"SYNTHETIC ORDER DEFINITION",Encoding.ASCII);
                File.WriteAllText(Path.Combine(modern,"helpertrinfo.dat"),"MODERN STATIC HELPER",Encoding.ASCII);
                File.WriteAllText(Path.Combine(legacy,"helpertrinfo.dat"),"LEGACY STATIC HELPER",Encoding.ASCII);
                File.WriteAllText(Path.Combine(root,"diagnostics.log"),"ACCOUNT12345678901 PASSWORD1111");
                File.WriteAllText(Path.Combine(root,"live-orders-private.xml"),"ACCOUNT12345678901");
                File.WriteAllText(Path.Combine(qry,"other.xtr"),"PRIVATE UNREQUESTED RESOURCE");
                string exported=IndiDefinitionExport.Save(starter,destination);
                Check(Path.GetDirectoryName(exported)==destination&&Path.GetFileName(exported).StartsWith("indi-query-definitions-",StringComparison.Ordinal)&&Path.GetExtension(exported)==".xml","export filename is unique, fixed-prefix XML under the requested destination");
                var xml=new XmlDocument{XmlResolver=null};xml.Load(exported);
                Check(xml.DocumentElement.Name=="IndiQueryDefinitions"&&xml.DocumentElement.GetAttribute("version")=="1"&&xml.DocumentElement.ChildNodes.Count==5,"export contains exactly four known query entries and one helper");
                var balance=Entry(xml,"qry/saba609q1.xtr");
                Check(balance.GetAttribute("status")=="Included"&&balance.GetAttribute("bytes")=="3"&&Convert.FromBase64String(balance.InnerText).SequenceEqual(body),"binary query definitions round-trip exactly without being interpreted or executed");
                Check(balance.GetAttribute("sha256")=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"&&balance.GetAttribute("encoding")=="base64","export records fixed byte length, lowercase SHA256 and encoding");
                Check(Entry(xml,"qry/saba102u1.xtr").GetAttribute("status")=="Missing","a missing optional query is explicit rather than fabricated");
                Check(Entry(xml,"qry/saba231q1.xtr").GetAttribute("status")=="Missing","a missing installed order-query layout remains explicit until it is captured");
                Check(Encoding.ASCII.GetString(Convert.FromBase64String(Entry(xml,"qry/config/helpertrinfo.dat").InnerText))=="MODERN STATIC HELPER","current qry/config helper takes priority over the older root/config helper");
                string text=File.ReadAllText(exported);
                Check(!text.Contains(root)&&!text.Contains(destination)&&!text.Contains("12345678901")&&!text.Contains("PASSWORD")&&!text.Contains("diagnostics.log")&&!text.Contains("live-orders")&&!text.Contains("other.xtr"),"export omits absolute installation paths and every account/log/unrequested file");
                Check(File.ReadAllBytes(Path.Combine(qry,"saba609q1.xtr")).SequenceEqual(body)&&new FileInfo(starter).Length==0,"export leaves vendor resources and starter unchanged");
                string second=IndiDefinitionExport.Save(starter,destination);
                Check(second!=exported&&File.Exists(exported)&&File.Exists(second),"repeated exports preserve existing output instead of overwriting it");
                byte[] ordersDefinition={2,8,0,1,255};File.WriteAllBytes(Path.Combine(qry,"saba231q1.xtr"),ordersDefinition);
                string withOrders=IndiDefinitionExport.Save(starter,destination);xml.Load(withOrders);
                var orders=Entry(xml,"qry/saba231q1.xtr");
                Check(orders.GetAttribute("status")=="Included"&&orders.GetAttribute("bytes")=="5"
                    &&Convert.FromBase64String(orders.InnerText).SequenceEqual(ordersDefinition),
                    "installed order-query definition is exported byte-for-byte without interpreting or invoking it");
                Check(File.ReadAllBytes(Path.Combine(qry,"saba231q1.xtr")).SequenceEqual(ordersDefinition),
                    "capturing installed order-query layout never changes the vendor definition");
                Check(!Directory.EnumerateFiles(destination,"*.tmp").Any(),"successful export publishes no temporary leftovers");
                File.Delete(Path.Combine(modern,"helpertrinfo.dat"));
                string fallback=IndiDefinitionExport.Save(starter,destination);xml.Load(fallback);
                Check(Encoding.ASCII.GetString(Convert.FromBase64String(Entry(xml,"config/helpertrinfo.dat").InnerText))=="LEGACY STATIC HELPER","missing current helper permits the fixed legacy fallback");
                File.Delete(Path.Combine(legacy,"helpertrinfo.dat"));
                string noHelper=IndiDefinitionExport.Save(starter,destination);xml.Load(noHelper);
                Check(Entry(xml,"config/helpertrinfo.dat").GetAttribute("status")=="Missing","missing helper does not hide available query definitions");
                File.WriteAllBytes(Path.Combine(qry,"saba102u1.xtr"),new byte[0]);
                string empty=IndiDefinitionExport.Save(starter,destination);xml.Load(empty);
                Check(Entry(xml,"qry/saba102u1.xtr").GetAttribute("status")=="Empty"&&Entry(xml,"qry/saba102u1.xtr").InnerText=="","empty definition cannot be reported as included data");
                using(var file=new FileStream(Path.Combine(qry,"saba102u1.xtr"),FileMode.Create,FileAccess.Write,FileShare.None))file.SetLength(1024*1024+1);
                string oversize=IndiDefinitionExport.Save(starter,destination);xml.Load(oversize);
                Check(Entry(xml,"qry/saba102u1.xtr").GetAttribute("status")=="TooLarge"&&Entry(xml,"qry/saba102u1.xtr").GetAttribute("bytes")=="1048577"&&Entry(xml,"qry/saba102u1.xtr").InnerText=="","oversized resources are described but never allocated or copied");
                string blocked=Path.Combine(root,"blocked-export");File.WriteAllText(blocked,"PRESERVE");
                Reject(()=>IndiDefinitionExport.Save(starter,blocked),"an unwritable destination produces an owned failure");
                Check(File.ReadAllText(blocked)=="PRESERVE"&&!Directory.EnumerateFiles(root,"*.tmp",SearchOption.AllDirectories).Any(),"failed export preserves existing files and removes incomplete temporary output");
                foreach(string invalid in new[]{null,IndiInstallation.StarterFileName,"\""+starter+"\"",starter+"\n",Path.Combine(root,"other.exe")})
                    Reject(()=>IndiDefinitionExport.Save(invalid,destination),"untrusted or missing starter path is rejected before reading resources");
                Reject(()=>IndiDefinitionExport.Save(starter,"relative-export"),"relative export directory is not resolved through the process working directory");
                File.Delete(starter);Reject(()=>IndiDefinitionExport.Save(starter,destination),"missing registered starter cannot authorize resource collection");
                return checks;
            }finally{Directory.Delete(root,true);}
        }
    }
}
