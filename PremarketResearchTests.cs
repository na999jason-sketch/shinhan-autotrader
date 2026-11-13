using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class PremarketResearchTests {
        static int checks;
        static readonly DateTime Target=new DateTime(2026,11,13);
        static readonly DateTime Now=new DateTime(2026,11,13,0,15,30,DateTimeKind.Utc);
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("PremarketResearchTests: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,name);}
        static Stock Stock(string code="005930"){return new Stock{Code=code,Name="Research stock",Market="0"};}
        static List<Bar> Bars(DateTime target,int count=25,int lastAge=1,int increment=100) {
            var result=new List<Bar>();
            for(int i=0;i<count;i++) {
                decimal close=10000+i*increment;
                result.Add(new Bar{Date=target.AddDays(-lastAge-count+1+i).ToString("yyyyMMdd",CultureInfo.InvariantCulture),
                    Open=close-20,High=close+100,Low=close-100,Close=close,Volume=100});
            }
            return result;
        }
        static RealtimeQuote Quote(decimal price=12501m,decimal volume=130m) {
            return new RealtimeQuote{Code="005930",Price=price,CumulativeVolume=volume,Clock="091530",MarketCode="1",SequenceConfirmed=true,ReceivedUtc=Now};
        }
        static PremarketReference Reference(StrategySettings settings=null,DateTime? target=null,string code="005930") {
            var date=target??Target;return PremarketResearch.Create(Stock(code),Bars(date),settings??new StrategySettings(),date);
        }
        public static int Run() {
            checks=0;var settings=new StrategySettings();var stock=Stock();var bars=Bars(Target);
            var reference=PremarketResearch.Create(stock,bars,settings,Target);
            Check(reference!=null&&Rules.Entry(stock.Code,stock.Name,bars,settings)==null,"a valid uptrend enters preparation without requiring yesterday's breakout or volume surge");
            Check(reference.Code=="005930"&&reference.Name==stock.Name&&reference.Market=="0"&&reference.TargetDateKorea==Target
                &&reference.LastCompletedBarDate=="20261112"&&reference.CompletedBarCount==25,"reference preserves stock identity and actual historical dates/count");
            Check(reference.FastAverage==12200m&&reference.SlowAverage==11450m&&reference.PriorHigh==12500m
                &&reference.AverageVolume==100m&&reference.ReferenceClose==12400m,"all statistics use the most recent completed windows including the last completed bar");

            var lookahead=Bars(Target);lookahead.Add(new Bar{Date="20261113",Open=0,High=0,Low=0,Close=0,Volume=0});
            lookahead.Add(new Bar{Date="20261116",Open=1000000,High=1000000,Low=1000000,Close=1000000,Volume=1000000});
            var withoutLookahead=PremarketResearch.Create(stock,lookahead,settings,Target);
            Check(withoutLookahead.PriorHigh==reference.PriorHigh&&withoutLookahead.AverageVolume==reference.AverageVolume
                &&withoutLookahead.FastAverage==reference.FastAverage&&withoutLookahead.SlowAverage==reference.SlowAverage
                &&withoutLookahead.CompletedBarCount==25&&withoutLookahead.LastCompletedBarDate=="20261112","unfinished target-day and future bars cannot change completed research facts");
            var shortHistory=Bars(Target,19);shortHistory.Add(new Bar{Date="20261113",Open=20000,High=21000,Low=19000,Close=20000,Volume=1000000});
            Check(PremarketResearch.Create(stock,shortHistory,settings,Target)==null,"an unfinished target-day bar cannot satisfy minimum completed history");
            var oldOutlier=Bars(Target);oldOutlier[0].High=1000000;oldOutlier[0].Volume=1000000;
            var trailing=PremarketResearch.Create(stock,oldOutlier,settings,Target);
            Check(trailing.PriorHigh==12500&&trailing.AverageVolume==100,"older bars outside configured breakout/volume windows cannot inflate thresholds");
            bars.Reverse();var unordered=PremarketResearch.Create(stock,bars,settings,Target);
            Check(unordered.PriorHigh==reference.PriorHigh&&unordered.FastAverage==reference.FastAverage,"valid reverse chart order produces identical completed statistics");
            Check(PremarketResearch.Create(stock,new Bar[0],settings,Target)==null&&PremarketResearch.Create(stock,null,settings,Target)==null,"missing chart data cannot create a watch reference");
            var malformed=Bars(Target);malformed[0].Date="20260230";
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"invalid historical calendar dates are rejected");
            malformed=Bars(Target);malformed.Add(malformed[0]);
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"duplicate chart dates are rejected rather than selecting a convenient copy");
            malformed=Bars(Target);malformed[0].High=1;
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"malformed completed OHLC is rejected even outside the trailing window");
            malformed=Bars(Target);malformed[0].Volume=-1;
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"negative historical volume is rejected");
            malformed=Bars(Target);malformed[0].Volume=1.5m;
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"fractional historical share volume is rejected");
            malformed=Bars(Target);foreach(var bar in malformed)bar.Volume=0;
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"a zero-volume history does not invent a volume reference");
            malformed=Bars(Target);malformed[malformed.Count-1]=null;
            Check(PremarketResearch.Create(stock,malformed,settings,Target)==null,"null completed observations do not silently shorten history");
            Check(PremarketResearch.Create(stock,Bars(Target,lastAge:11),settings,Target)==null,"completed data older than ten calendar days is rejected");
            Check(PremarketResearch.Create(stock,Bars(Target,lastAge:10),settings,Target)!=null,"a bounded holiday gap does not pretend to know an exchange holiday calendar");
            var monday=new DateTime(2026,11,16);var afterWeekend=PremarketResearch.Create(stock,Bars(monday,lastAge:3),settings,monday);
            Check(afterWeekend!=null&&afterWeekend.LastCompletedBarDate=="20261113","Friday's completed data supports Monday preparation without fabricated weekend candles");
            var noTrend=Bars(Target,increment:-100);
            Check(PremarketResearch.Create(stock,noTrend,settings,Target)==null,"a declining completed trend is not a potential entry");
            var limits=settings.Copy();limits.MinPrice=13000;
            Check(PremarketResearch.Create(stock,Bars(Target),limits,Target)==null,"completed reference close below minimum price is screened out");
            limits=settings.Copy();limits.MaxPrice=12000;
            Check(PremarketResearch.Create(stock,Bars(Target),limits,Target)==null,"completed reference close above maximum price is screened out");
            var unknownMarket=Stock();unknownMarket.Market="2";
            Check(PremarketResearch.Create(unknownMarket,Bars(Target),settings,Target)==null,"a nonordinary market identifier cannot enter the stock watchlist");
            Reject(()=>PremarketResearch.Create(stock,Bars(Target),settings,DateTime.SpecifyKind(Target,DateTimeKind.Utc)),"target date must explicitly represent Korea wall date rather than UTC");
            Reject(()=>PremarketResearch.Create(stock,Bars(Target),settings,Target.AddHours(1)),"target date cannot hide a time component");
            Reject(()=>PremarketResearch.Create(stock,Bars(Target),settings,new DateTime(2026,11,14)),"a weekend target is not inferred to be an exchange session");
            var invalid=settings.Copy();invalid.FastPeriod=invalid.SlowPeriod;
            Reject(()=>PremarketResearch.Create(stock,Bars(Target),invalid,Target),"invalid strategy is rejected before research");

            var signal=PremarketResearch.EvaluateLive(reference,Quote(),settings,Now);
            Check(signal!=null&&signal.SignalDate=="20261113"&&signal.Price==12501m&&reference.LastCompletedBarDate=="20261112",
                "a confirmed live breakout produces today's signal and actual quote price without rewriting historical facts");
            Check(signal.Score==0.91m&&signal.Reason.Contains("20261112")
                &&signal.Reason.Contains("당일 누적거래량"),"live score and reason state historical baseline plus actual current-day volume");
            Check(PremarketResearch.EvaluateLive(reference,Quote(price:12500),settings,Now)==null,"touching the previous high is not a breakout");
            Check(PremarketResearch.EvaluateLive(reference,Quote(volume:129),settings,Now)==null
                &&PremarketResearch.EvaluateLive(reference,Quote(volume:130),settings,Now)!=null,"actual cumulative volume must reach the completed-average multiplier including its exact boundary");
            Check(PremarketResearch.LiveStatus(reference,Quote(price:12500),settings,Now)=="현재가 12500 / 돌파기준 12500 미돌파",
                "a fresh observed quote below the breakout displays the actual unmet price threshold rather than a realtime wait");
            Check(PremarketResearch.LiveStatus(reference,Quote(volume:129),settings,Now)=="누적거래량 129 / 기준 130 미달",
                "a fresh breakout with insufficient actual volume displays the exact volume and threshold");
            string liveStatus;var described=PremarketResearch.EvaluateLive(reference,Quote(),settings,Now,out liveStatus);
            Check(described!=null&&described.Price==signal.Price&&described.Score==signal.Score&&liveStatus.Contains("매수 조건 충족"),
                "entry status and candidate use the same decision path without changing eligibility or score");
            Check(PremarketResearch.LiveStatus(reference,null,settings,Now)=="실시간 연속 시세 확인 대기",
                "missing realtime evidence retains an explicit source-data wait");
            var quote=Quote();quote.SequenceConfirmed=false;
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"price-only or unconfirmed SC cannot promote preparation to an entry");
            quote=Quote();quote.MarketCode="2";
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"auction/other-market SC is not a confirmed regular-session entry");
            quote=Quote();quote.Code="000660";
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"another symbol's live event cannot trigger a prepared stock");
            quote=Quote();quote.ReceivedUtc=Now.AddSeconds(-46);
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"old receipt timestamps cannot be refreshed by a convenient broker clock");
            Check(PremarketResearch.LiveStatus(reference,quote,settings,Now)=="최신 체결시각 확인 대기",
                "stale quotes cannot be described as merely waiting for a price breakout");
            quote=Quote();quote.Clock="091400";
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"stale broker trade clock cannot be refreshed by a new receipt timestamp");
            quote=Quote();quote.ReceivedUtc=Now.AddSeconds(1);
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"future quote receipt times are rejected");
            quote=Quote();quote.ReceivedUtc=DateTime.SpecifyKind(Now,DateTimeKind.Unspecified);
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"quote receipt timezone must be explicit UTC");
            quote=Quote(volume:130.5m);
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,Now)==null,"fractional live share volume cannot satisfy a surge");
            Check(PremarketResearch.EvaluateLive(reference,Quote(price:400001m),settings,Now)==null,"live entry also enforces the configured price ceiling");
            Check(PremarketResearch.EvaluateLive(Reference(target:new DateTime(2026,11,12)),Quote(),settings,Now)==null
                &&PremarketResearch.EvaluateLive(afterWeekend,Quote(),settings,Now)==null,"historical or future-session references cannot masquerade as today's preparation");
            quote=Quote();var preopen=new DateTime(2026,11,12,23,59,30,DateTimeKind.Utc);quote.ReceivedUtc=preopen;quote.Clock="085930";
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,preopen)==null,"preparation itself cannot create a preopen entry");
            quote=Quote();var close=new DateTime(2026,11,13,6,20,0,DateTimeKind.Utc);quote.ReceivedUtc=close;quote.Clock="152000";
            Check(PremarketResearch.EvaluateLive(reference,quote,settings,close)==null,"automatic entry stops at the configured regular-session cutoff");
            Reject(()=>PremarketResearch.EvaluateLive(reference,Quote(),settings,DateTime.SpecifyKind(Now,DateTimeKind.Local)),"live evaluation requires explicit UTC");
            var edited=settings.Copy();edited.VolumeMultiplier=2;
            Check(PremarketResearch.EvaluateLive(reference,Quote(volume:1000),edited,Now)==null,"entry strategy edits require rebuilding a prepared reference even if the new threshold would pass");
            Check(PremarketResearch.LiveStatus(reference,Quote(),edited,Now).Contains("전략 변경"),
                "status reports strategy mismatch instead of applying a newly configured threshold to old research");
            Check(PremarketResearch.LiveStatus(Reference(target:new DateTime(2026,11,12)),Quote(),settings,Now).Contains("대상 거래일 불일치"),
                "status reports an old target day without implying that current live prices can revive its reference");
            edited=settings.Copy();edited.FastPeriod=4;
            Check(PremarketResearch.EvaluateLive(reference,Quote(),edited,Now)==null,"moving-average period edits cannot silently reuse old statistics");
            edited=settings.Copy();edited.StopLossPercent=5;
            Check(PremarketResearch.EvaluateLive(reference,Quote(),edited,Now)!=null,"an exit-only setting does not alter completed entry statistics");
            stock.Code="000660";stock.Name="Changed";bars[0].Close=1;settings.FastPeriod=4;
            Check(reference.Code=="005930"&&reference.Name=="Research stock"&&reference.FastAverage==12200
                &&PremarketResearch.EvaluateLive(reference,Quote(),new StrategySettings(),Now)!=null,"mutable source objects cannot change an existing research reference");

            var all=new List<PremarketReference>();
            for(int i=1;i<=205;i++)all.Add(Reference(code:i.ToString("D6",CultureInfo.InvariantCulture)));
            all.Reverse();var ranked=PremarketResearch.Rank(all);
            Check(ranked.Count==200&&ranked[0].Code=="000001"&&ranked[199].Code=="000200","watchlist respects the two-hundred RT limit and deterministic symbol tie breaking");
            Check(PremarketResearch.Rank(all,3).Count==3&&PremarketResearch.Rank(all,0).Count==0,"caller can reserve realtime slots for existing holdings");
            var strong=PremarketResearch.Create(Stock("999999"),Bars(Target,increment:200),new StrategySettings(),Target);
            Check(PremarketResearch.Rank(new[]{all[0],strong},1)[0].Code=="999999","bounded watchlist ranks stronger near-high trends rather than first chart response order");
            bool readOnly=false;try{ranked.Add(reference);}catch(NotSupportedException){readOnly=true;}
            Check(readOnly,"a ranked watchlist cannot be mutated to exceed its subscription capacity");
            Reject(()=>PremarketResearch.Rank(all,201),"capacity above documented realtime limit is rejected");
            Reject(()=>PremarketResearch.Rank(new[]{reference,afterWeekend}),"mixed target dates cannot form one subscription watchlist");
            var alternate=new StrategySettings();alternate.VolumeMultiplier=2;
            Reject(()=>PremarketResearch.Rank(new[]{reference,Reference(alternate,code:"000660")}),"mixed entry strategies cannot form one prepared watchlist");
            Reject(()=>PremarketResearch.Rank(new[]{reference,reference}),"duplicate symbols cannot consume multiple watchlist positions");
            return checks;
        }
    }
}
