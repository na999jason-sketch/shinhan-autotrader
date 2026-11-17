using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ShinhanNativeTrader {
    // Resolves the vendor starter without launching it or changing the process directory.
    // In the x86 executable Registry.ClassesRoot uses the registered 32-bit OCX view.
    public static class IndiInstallation {
        public const string StarterFileName = "giexpertstarter.exe";
        const string DocumentedStarter = @"C:\SHINHAN-i\indi\giexpertstarter.exe";
        const string BalanceSchemaReferenceHash = "8578b538bf8c8848fa91e039df43f47cc180b8987881e1b09a1578dead4235ec";
        const int BalanceSchemaReferenceLength = 2981;
        const int MaximumSchemaLength = 1024 * 1024;
        const string BalanceSchemaUnavailable = "SABA609Q1 installed schema: unavailable";

        // Call only with the starter resolved from the registered Indi installation.
        // Read one fixed query file and report metadata; never log its path or contents.
        public static string DescribeBalanceSchema(string starterPath) {
            try {
                if(string.IsNullOrWhiteSpace(starterPath) || starterPath.IndexOf('"') >= 0 ||
                   !Path.IsPathRooted(starterPath)) return BalanceSchemaUnavailable;
                foreach(char character in starterPath) if(char.IsControl(character)) return BalanceSchemaUnavailable;
                var starter = Path.GetFullPath(starterPath);
                if(!string.Equals(Path.GetFileName(starter),StarterFileName,StringComparison.OrdinalIgnoreCase) ||
                   !File.Exists(starter)) return BalanceSchemaUnavailable;
                var directory = Path.GetDirectoryName(starter);
                if(string.IsNullOrEmpty(directory)) return BalanceSchemaUnavailable;
                var schema = Path.Combine(directory,"qry","saba609q1.xtr");
                byte[] bytes;
                using(var stream = new FileStream(schema,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(stream.Length < 0 || stream.Length > MaximumSchemaLength) return BalanceSchemaUnavailable;
                    bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while(offset < bytes.Length) {
                        int read = stream.Read(bytes,offset,bytes.Length-offset);
                        if(read == 0) return BalanceSchemaUnavailable;
                        offset += read;
                    }
                    // Reject a concurrent length change; never hash beyond the bound.
                    if(stream.ReadByte() != -1) return BalanceSchemaUnavailable;
                }
                string digest;
                using(var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                bool matches = bytes.Length == BalanceSchemaReferenceLength && string.Equals(digest,BalanceSchemaReferenceHash,StringComparison.Ordinal);
                return "SABA609Q1 installed schema: bytes=" + bytes.Length.ToString(CultureInfo.InvariantCulture)
                    + "; SHA256=" + digest + "; referenceMatch=" + matches;
            } catch {
                // Schema diagnostics must not interrupt a query or disclose a local path.
                return BalanceSchemaUnavailable;
            }
        }

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
