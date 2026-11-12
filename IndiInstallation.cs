using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace ShinhanNativeTrader {
    // Resolves the vendor starter without launching it or changing the process directory.
    // In the x86 executable Registry.ClassesRoot uses the registered 32-bit OCX view.
    public static class IndiInstallation {
        public const string StarterFileName = "giexpertstarter.exe";
        const string DocumentedStarter = @"C:\SHINHAN-i\indi\giexpertstarter.exe";

        public static string FindStarter(Guid classId) {
            if(classId == Guid.Empty) return null;
            try {
                using(var registration = Registry.ClassesRoot.OpenSubKey("CLSID\\" + classId.ToString("B") + "\\InprocServer32",false)) {
                    if(registration != null) {
                        var raw = registration.GetValue(null,null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if(raw != null) {
                            var candidate = FindStarterNearModule(Environment.ExpandEnvironmentVariables(raw),File.Exists);
                            if(candidate != null) return candidate;
                        }
                    }
                }
            } catch(SecurityException) { }
            catch(UnauthorizedAccessException) { }
            catch(IOException) { }

            // No search of PATH, current directory or arbitrary executable names.
            // This documented vendor location is accepted only when it actually exists.
            return File.Exists(DocumentedStarter) ? DocumentedStarter : null;
        }

        public static string FindStarterNearModule(string modulePath,Func<string,bool> fileExists) {
            if(fileExists == null) throw new ArgumentNullException("fileExists");
            if(string.IsNullOrWhiteSpace(modulePath)) return null;
            var path = modulePath.Trim();
            if(path.Length > 1 && path[0] == '"' && path[path.Length-1] == '"') path = path.Substring(1,path.Length-2);
            if(path.IndexOf('"') >= 0 || path.IndexOf('\0') >= 0) return null;
            for(int index=0;index<path.Length;index++) if(char.IsControl(path[index])) return null;
            try {
                if(!Path.IsPathRooted(path)) return null;
                var extension = Path.GetExtension(path);
                if(!string.Equals(extension,".ocx",StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(extension,".dll",StringComparison.OrdinalIgnoreCase)) return null;
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                if(string.IsNullOrEmpty(directory)) return null;
                var starter = Path.Combine(directory,StarterFileName);
                return fileExists(starter) ? starter : null;
            } catch(ArgumentException) { return null; }
            catch(NotSupportedException) { return null; }
            catch(PathTooLongException) { return null; }
            catch(IOException) { return null; }
            catch(UnauthorizedAccessException) { return null; }
            catch(SecurityException) { return null; }
        }
    }
}
