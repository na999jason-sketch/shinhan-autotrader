using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace ShinhanNativeTrader {
    public sealed class AccountRiskHigh {
        public string AccountKey,Code;
        public decimal AveragePrice,High;
    }
    public sealed class AccountAppSettings {
        public int Version=1;
        public decimal BudgetLimit;
        public StrategySettings Strategy=new StrategySettings();
        public List<AccountRiskHigh> Highs=new List<AccountRiskHigh>();
    }

    // Saves strategy limits and quote-observed risk highs only. It never opens
    // the old simulation file or stores balances, credentials or invented fills.
    public sealed class AccountAppSettingsStore : IDisposable {
        static readonly XmlSerializer Serializer=new XmlSerializer(typeof(AccountAppSettings));
        readonly string path;
        readonly FileStream lease;
        AccountAppSettings current;
        bool disposed,faulted;
        public AccountAppSettingsStore(string settingsPath) {
            if(string.IsNullOrWhiteSpace(settingsPath))throw new ArgumentException("Settings path required.");
            path=Path.GetFullPath(settingsPath);Directory.CreateDirectory(Path.GetDirectoryName(path));
            try {
                lease=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(File.Exists(path))using(var file=File.OpenRead(path))current=(AccountAppSettings)Serializer.Deserialize(file);
                else current=new AccountAppSettings();
                Validate(current);
            }catch{if(lease!=null)lease.Dispose();throw;}
        }
        public AccountAppSettings Snapshot {get{Usable();return Copy(current);}}
        public bool Faulted {get{return faulted;}}
        void Usable(){if(disposed)throw new ObjectDisposedException("AccountAppSettingsStore");if(faulted)throw new InvalidOperationException("Strategy storage is unavailable.");}
        static bool Key(string key){return key!=null&&key.Length==64&&key.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));}
        static void Validate(AccountAppSettings value) {
            if(value==null||value.Version!=1||value.Strategy==null||value.Highs==null||value.BudgetLimit<0||value.BudgetLimit>1000000000m)
                throw new InvalidOperationException("Invalid account strategy settings.");
            value.Strategy.Validate();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var high in value.Highs)if(high==null||!Key(high.AccountKey)||string.IsNullOrEmpty(high.Code)||Rules.Code(high.Code)!=high.Code
                ||high.AveragePrice<=0||high.High<high.AveragePrice||!seen.Add(high.AccountKey+":"+high.Code))
                throw new InvalidOperationException("Invalid account risk high record.");
        }
        static AccountAppSettings Copy(AccountAppSettings value) {
            return new AccountAppSettings {Version=value.Version,BudgetLimit=value.BudgetLimit,Strategy=value.Strategy.Copy(),
                Highs=value.Highs.Select(x=>new AccountRiskHigh{AccountKey=x.AccountKey,Code=x.Code,AveragePrice=x.AveragePrice,High=x.High}).ToList()};
        }
        void Save(AccountAppSettings value) {
            Usable();Validate(value);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {Serializer.Serialize(file,value);file.Flush(true);}
                if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
                current=value;
            }catch{faulted=true;throw;}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void Update(StrategySettings strategy,decimal budgetLimit) {
            if(strategy==null)throw new ArgumentNullException("strategy");
            var next=Snapshot;next.Strategy=strategy.Copy();next.BudgetLimit=budgetLimit;Save(next);
        }
        public decimal High(string accountKey,string code,decimal averagePrice) {
            Usable();if(!Key(accountKey)||string.IsNullOrEmpty(code)||Rules.Code(code)!=code||averagePrice<=0)
                throw new ArgumentException("A bound account, symbol and average price are required.");
            var high=current.Highs.SingleOrDefault(x=>x.AccountKey==accountKey&&x.Code==code&&x.AveragePrice==averagePrice);
            return high==null?averagePrice:high.High;
        }
        public bool ObserveHigh(string accountKey,Holding holding,IndiReply quote,DateTime utc) {
            Usable();if(!Key(accountKey))throw new ArgumentException("Account key required.");
            if(holding==null||holding.Quantity<=0||holding.AvgPrice<=0||quote==null||quote.Code!=holding.Code||!MarketTime.CanUseQuote(quote,utc))return false;
            var next=Snapshot;var high=next.Highs.SingleOrDefault(x=>x.AccountKey==accountKey&&x.Code==holding.Code);
            if(high==null){high=new AccountRiskHigh{AccountKey=accountKey,Code=holding.Code,AveragePrice=holding.AvgPrice,High=holding.AvgPrice};next.Highs.Add(high);}
            else if(high.AveragePrice!=holding.AvgPrice){high.AveragePrice=holding.AvgPrice;high.High=holding.AvgPrice;}
            high.High=Math.Max(high.High,quote.Price);
            var previous=current.Highs.SingleOrDefault(x=>x.AccountKey==accountKey&&x.Code==holding.Code);
            if(previous!=null&&previous.AveragePrice==high.AveragePrice&&previous.High==high.High)return false;
            Save(next);return true;
        }
        public void RefreshPositions(string accountKey,IEnumerable<Holding> holdings) {
            if(!Key(accountKey)||holdings==null)throw new ArgumentException("Account holdings required.");
            var live=holdings.Where(x=>x!=null&&x.Quantity>0&&x.AvgPrice>0).ToDictionary(x=>x.Code,x=>x.AvgPrice,StringComparer.Ordinal);
            var next=Snapshot;int removed=next.Highs.RemoveAll(x=>x.AccountKey==accountKey&&(!live.ContainsKey(x.Code)||live[x.Code]!=x.AveragePrice));
            if(removed>0)Save(next);
        }
        public void Dispose(){if(disposed)return;disposed=true;if(lease!=null)lease.Dispose();}
    }
}
