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
            return checks;
        }
    }
}
