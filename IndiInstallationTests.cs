using System;
using System.IO;

namespace ShinhanNativeTrader {
    public static class IndiInstallationTests {
        static int checks;
        static void Check(bool condition,string label) { checks++;if(!condition)throw new Exception("Indi installation test failed: "+label); }
        public static int Run() {
            checks=0;
            var directory=Path.Combine(Path.GetTempPath(),"Shinhan resolver with spaces");
            var module=Path.Combine(directory,"GiExpertControl.ocx");
            var expected=Path.Combine(directory,IndiInstallation.StarterFileName);
            int reads=0;
            Func<string,bool> exists=path=>{reads++;return path==expected;};
            Check(IndiInstallation.FindStarterNearModule(module,exists)==expected,"starter next to registered OCX");
            Check(reads==1,"only same-directory starter is probed");
            Check(IndiInstallation.FindStarterNearModule("  \""+module+"\"  ",exists)==expected,"one quoted module path with spaces");
            Check(IndiInstallation.FindStarterNearModule(Path.ChangeExtension(module,".DLL"),exists)==expected,"case-insensitive DLL registration");
            Check(IndiInstallation.FindStarterNearModule(module,path=>false)==null,"missing starter is not fabricated");
            Check(IndiInstallation.FindStarterNearModule(null,exists)==null,"null registration is unsupported");
            Check(IndiInstallation.FindStarterNearModule("  ",exists)==null,"blank registration is unsupported");
            Check(IndiInstallation.FindStarterNearModule("GiExpertControl.ocx",exists)==null,"relative module cannot resolve from current directory");
            Check(IndiInstallation.FindStarterNearModule("\""+module+"\" -arg",exists)==null,"quoted command-line arguments are rejected");
            Check(IndiInstallation.FindStarterNearModule(module+" -arg",exists)==null,"unquoted command-line arguments are rejected");
            Check(IndiInstallation.FindStarterNearModule(module+"\"",exists)==null,"unmatched quote is rejected");
            Check(IndiInstallation.FindStarterNearModule(module+"\r\n",exists)==expected,"outer whitespace is trimmed safely");
            Check(IndiInstallation.FindStarterNearModule(module.Replace("GiExpert","Gi\nExpert"),exists)==null,"embedded control character is rejected");
            Check(IndiInstallation.FindStarterNearModule(Path.ChangeExtension(module,".exe"),exists)==null,"executable registration is not treated as an OCX");
            Check(IndiInstallation.FindStarterNearModule(module+"\0",exists)==null,"embedded null is rejected");
            bool nullProbeRejected=false;
            try { IndiInstallation.FindStarterNearModule(module,null); } catch(ArgumentNullException) { nullProbeRejected=true; }
            Check(nullProbeRejected,"existence probe is required");
            var before=Environment.CurrentDirectory;
            IndiInstallation.FindStarterNearModule(module,exists);
            Check(Environment.CurrentDirectory==before,"resolution does not change current directory");
            Check(IndiInstallation.FindStarter(Guid.Empty)==null,"empty CLSID does not read registry or choose a starter");
            TestBalanceSchema();
            return checks;
        }
        static void TestBalanceSchema() {
            const string unavailable="SABA609Q1 installed schema: unavailable";
            var directory=Path.Combine(Path.GetTempPath(),"ShinhanSchema-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                var starter=Path.Combine(directory,IndiInstallation.StarterFileName);
                var schemaDirectory=Path.Combine(directory,"qry");
                Directory.CreateDirectory(schemaDirectory);
                var schema=Path.Combine(schemaDirectory,"saba609q1.xtr");
                File.WriteAllBytes(starter,new byte[0]);
                Check(IndiInstallation.DescribeBalanceSchema(starter)==unavailable,"missing schema returns a fixed unavailable result");
                File.WriteAllBytes(schema,new byte[]{97,98,99});
                var expected="SABA609Q1 installed schema: bytes=3; SHA256=ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad; referenceMatch=False";
                Check(IndiInstallation.DescribeBalanceSchema(starter)==expected,"installed schema fingerprint uses SHA256 and lowercase hex");
                Check(!IndiInstallation.DescribeBalanceSchema(starter).Contains(directory),"schema fingerprint does not expose installation path");
                Check(File.ReadAllBytes(schema).Length==3 && File.ReadAllBytes(starter).Length==0,"fingerprinting does not mutate files");
                Check(IndiInstallation.DescribeBalanceSchema(null)==unavailable,"null starter path is unavailable");
                Check(IndiInstallation.DescribeBalanceSchema(IndiInstallation.StarterFileName)==unavailable,"relative starter path is unavailable");
                var wrong=Path.Combine(directory,"other.exe");File.WriteAllBytes(wrong,new byte[0]);
                Check(IndiInstallation.DescribeBalanceSchema(wrong)==unavailable,"different executable cannot select a schema directory");
                Check(IndiInstallation.DescribeBalanceSchema("\""+starter+"\"")==unavailable,"quoted noncanonical starter path is unavailable");
                Check(IndiInstallation.DescribeBalanceSchema(starter+" -arg")==unavailable,"starter command arguments are unavailable");
                File.Delete(starter);
                Check(IndiInstallation.DescribeBalanceSchema(starter)==unavailable,"missing starter cannot select a schema directory");
                File.WriteAllBytes(starter,new byte[0]);
                using(var file=new FileStream(schema,FileMode.Create,FileAccess.Write,FileShare.None)) file.SetLength(1024*1024+1);
                Check(IndiInstallation.DescribeBalanceSchema(starter)==unavailable,"oversized schema is not hashed");
                File.Delete(schema);Directory.CreateDirectory(schema);
                Check(IndiInstallation.DescribeBalanceSchema(starter)==unavailable,"unreadable schema path is unavailable without throwing");
            } finally { Directory.Delete(directory,true); }
        }
    }
}
