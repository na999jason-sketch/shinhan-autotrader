using System;
using System.Collections.Generic;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class RealtimeIndiTests {
        static int checks;
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("RealtimeIndiTests: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}catch(ArgumentException){rejected=true;}Check(rejected,name);}
        static Dictionary<int,object> Quote(string clock="091530",string volume="100",string price="70000",string code="005930",string market="1") {
            return new Dictionary<int,object>{{1,code},{2,clock},{3,price},{7,volume},{17,market}};
        }
        static Dictionary<int,object> Order(string processing="03",string account="12345678901") {
            return new Dictionary<int,object>{{0,processing},{2,account},{4,"01"},{5,"123"},{6,"0"},{8,"02"},{9,"02"},
                {10,processing=="04"||processing=="05"?"02":processing=="09"||processing=="19"||processing=="98"||processing=="99"?"03":"01"},{11,"A005930"},
                {14,"10"},{15,"70000"},{16,"4"},{19,"0"},{20,"6"},{21,"091529"},{22,"F0004"},{23,"1"},{24,"69900"},{25,"091530"},{32,"0"}};
        }
        static Dictionary<int,object> Balance(string account="12345678901") {
            return new Dictionary<int,object>{{0,account},{1,"01"},{2,"A005930"},{3,"0"},{6,"5"},{7,"70000"},{8,"1"},{9,"2"},{20,"71000"}};
        }
        static object Field(Dictionary<int,object> fields,int index){object value;return fields.TryGetValue(index,out value)?value:"";}
        public static int Run() {
            checks=0;var utc=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
            var fields=Quote();var raw=RealtimeRecordParser.ReadQuote(index=>Field(fields,index),utc);
            Check(raw.Code=="005930"&&raw.Clock=="091530"&&raw.Price==70000&&raw.CumulativeVolume==100&&raw.MarketCode=="1","SC fields use documented symbol1 clock2 price3 cumulative7 market17");
            Check(!raw.SequenceConfirmed&&!raw.ToReply().HasTradeClock,"one realtime payload does not certify a live sequence");
            fields=Quote(price:"-70,000");Check(RealtimeRecordParser.ReadQuote(index=>Field(fields,index),utc).Price==70000,"signed broker realtime price is normalized");
            fields=Quote(clock:"");Check(RealtimeRecordParser.ReadQuote(index=>Field(fields,index),utc).Clock=="","absent clock stays absent and cannot be fabricated from receipt time");
            fields=Quote(code:"");Reject(()=>RealtimeRecordParser.ReadQuote(index=>Field(fields,index),utc),"missing realtime symbol is rejected");
            fields=Quote(price:"0");Reject(()=>RealtimeRecordParser.ReadQuote(index=>Field(fields,index),utc),"zero realtime price is rejected");
            fields=Quote(volume:"1.5");Reject(()=>RealtimeRecordParser.ReadQuote(index=>Field(fields,index),utc),"fractional domestic share cumulative volume is rejected");
            fields=Quote();Reject(()=>RealtimeRecordParser.ReadQuote(index=>Field(fields,index),DateTime.SpecifyKind(utc,DateTimeKind.Unspecified)),"receipt timestamp requires UTC");

            var processor=new RealtimeEventProcessor(index=>Field(fields,index));processor.Track("005930");
            int received=0;RealtimeQuote quoted=null;processor.QuoteReceived+=value=>{received++;quoted=value;};
            processor.Process("SC",utc);RealtimeQuote ready;
            Check(received==1&&!quoted.SequenceConfirmed&&!processor.TryGetQuote("005930",utc,out ready),"initial cached tick can display but cannot feed trading");
            processor.Process("SC",utc.AddSeconds(1));Check(!quoted.SequenceConfirmed,"identical replay cannot certify a live feed");
            fields=Quote(clock:"091531",volume:"101");processor.Process("SC",utc.AddSeconds(1));
            Check(quoted.SequenceConfirmed&&processor.TryGetQuote("005930",utc.AddSeconds(1),out ready),"advancing time and cumulative volume certify the observed sequence");
            quoted.Price=1;processor.TryGetQuote("005930",utc.AddSeconds(1),out ready);Check(ready.Price==70000,"event subscribers cannot mutate cached quote evidence");
            ready.Price=2;processor.TryGetQuote("005930",utc.AddSeconds(1),out ready);Check(ready.Price==70000,"query callers receive detached realtime snapshots");
            Check(!processor.TryGetQuote("005930",utc.AddSeconds(50),out ready),"stale exchange clock and receipt do not become fresh through polling");
            Check(!processor.TryGetQuote("005930",utc,out ready),"future receipt evidence cannot be consumed");
            int before=received;fields=Quote(code:"000660");processor.Process("SC",utc);Check(received==before&&!processor.Failed,"untracked orphan quote cannot affect active holdings");
            fields=Quote(clock:"091532",volume:"99");processor.Process("SC",utc.AddSeconds(2));Check(!quoted.SequenceConfirmed,"volume regression invalidates prior source progression");
            fields=Quote(clock:"091533",volume:"100");processor.Process("SC",utc.AddSeconds(3));Check(quoted.SequenceConfirmed,"subsequent advancing events can establish a new progression");
            fields=Quote(clock:"091534",volume:"101",market:"4");processor.Process("SC",utc.AddSeconds(4));Check(!quoted.SequenceConfirmed&&!quoted.ToReply().HasTradeClock,"after-hours market phase cannot provide regular-session trade authorization");
            fields=Quote(clock:"091535",volume:"102");processor.Process("SC",utc.AddSeconds(5));Check(!quoted.SequenceConfirmed,"first tick after changing market phase remains unconfirmed");
            fields=Quote(clock:"091536",volume:"103");processor.Process("SC",utc.AddSeconds(6));Check(quoted.SequenceConfirmed,"second regular-session advancing tick restores confirmed source state");
            processor.Invalidate();Check(!processor.TryGetQuote("005930",utc.AddSeconds(6),out ready),"system invalidation drops prior quote evidence");
            processor.Untrack("005930");processor.Process("SC",utc.AddSeconds(6));Check(received>0&&!processor.TryGetQuote("005930",utc.AddSeconds(6),out ready),"removed subscriptions no longer feed readiness");
            processor.Dispose();Reject(()=>processor.Track("005930"),"disposed processing session cannot be reused");

            fields=Order();var trade=RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc);
            Check(trade.IsExecution&&trade.ExecutionId=="F0004"&&trade.IndividualFillQuantity==1&&trade.CumulativeFilledQuantity==4,"AA individual quantity23 is distinct from cumulative quantity16");
            Check(trade.Code=="005930"&&trade.BrokerOrderNumber=="123"&&trade.SideCode=="02"&&trade.IndividualFillPrice==69900&&trade.Clock=="091530","AA execution fields retain broker identifiers and actual fill price/time");
            Check(trade.TradeTypeCode=="02"&&trade.OrderStatusCode=="01"&&trade.OriginalResultCode=="0","AA exposes exact cash trade9 order status10 and result-role32 evidence");
            Check(!typeof(CashOrderEvent).GetFields().Any(x=>x.Name=="Account"||x.Name=="Password"||x.Name=="AccountName"),"execution DTO contains no credentials or account name");
            fields=Order("00");fields[22]="bad unused ID";fields[23]="bad unused qty";
            var acknowledged=RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc);
            Check(!acknowledged.IsExecution&&acknowledged.IndividualFillQuantity==0&&acknowledged.ExecutionId=="","order acknowledgement does not fabricate a fill from cumulative fields");
            fields=Order("04");Check(RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc).IsAmendmentConfirmation,"AA amendment confirmation is classified separately");
            fields=Order("05");Check(RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc).IsCancellationConfirmation,"AA cancellation confirmation is classified separately");
            fields=Order("09");fields[5]="0";Check(RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc).IsRejection,"explicit rejected order can lack an accepted broker order number");
            fields=Order();fields[9]="04";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"credit buy profile cannot masquerade as a cash buy solely by product and side");
            fields=Order();fields[8]="01";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"cash trade type must match the documented sell or buy side");
            fields=Order();fields[8]="01";fields[9]="01";Check(RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc).IsExecution,"explicit cash sell profile remains supported");
            fields=Order("04");fields[10]="01";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"amend receipt status cannot be interpreted as confirmation");
            fields=Order("09");fields[10]="01";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"rejection requires the broker rejection status");
            fields=Order();fields[32]="2";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"unknown original-result role cannot enter order evidence");
            fields=Order();fields[32]="1";fields[22]="not an execution";fields[23]="not a fill";
            var echo=RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc);
            Check(!echo.IsExecution&&echo.IndividualFillQuantity==0&&echo.ExecutionId=="","original-result echo never becomes another individual execution");
            fields=Order();fields[22]="";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"missing unique execution identifier cannot be replaced by time or cumulative count");
            fields=Order();fields[23]="1.5";Reject(()=>RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc),"fractional execution quantity is rejected");
            fields=new Dictionary<int,object>{{2,"99999999999"}};Check(RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc)==null,"other account is filtered before reading or emitting financial fields");
            fields=Order();fields[4]="02";Check(RealtimeRecordParser.ReadOrder(index=>Field(fields,index),"12345678901",utc)==null,"other account product cannot enter domestic cash order evidence");

            fields=Balance();var balance=RealtimeRecordParser.ReadBalance(index=>Field(fields,index),"12345678901",utc);
            Check(balance.Code=="005930"&&balance.Quantity==5&&balance.AveragePrice==70000&&balance.OrderableQuantity==1&&balance.SellPendingQuantity==2,"AD authoritative orderable8 is preserved rather than inferred from holding6 minus pending9");
            Check(!typeof(CashBalanceEvent).GetFields().Any(x=>x.Name=="Account"||x.Name=="AccountName"),"balance DTO omits account identity and free text");
            fields=Balance("99999999999");Check(RealtimeRecordParser.ReadBalance(index=>Field(fields,index),"12345678901",utc)==null,"AD filters another account before financial publication");
            fields=Balance();fields[6]="-1";Reject(()=>RealtimeRecordParser.ReadBalance(index=>Field(fields,index),"12345678901",utc),"unexpected negative cash holding does not create sell capacity");

            fields=Order();processor=new RealtimeEventProcessor(index=>Field(fields,index));processor.BindAccount("12345678901");int fills=0;processor.OrderReceived+=value=>{fills++;};
            processor.Process("AA",utc);processor.Process("AA",utc);
            Check(fills==2&&processor.AccountStreamObserved,"duplicate execution events preserve their broker ID for journal-level idempotency");
            processor.Invalidate();Check(!processor.AccountStreamObserved,"system change invalidates account-event observation separately from registration");
            fields=Balance();int balances=0;processor.BalanceReceived+=value=>balances++;processor.Process("AD",utc);
            Check(balances==1&&processor.AccountStreamObserved,"filtered valid balance callback establishes actual event observation");
            Reject(()=>processor.BindAccount("99999999999"),"switching account requires a separate callback session");processor.Dispose();
            fields=Quote();processor=new RealtimeEventProcessor(index=>{throw new InvalidOperationException("SECRET account12345678901 password1111");});processor.Track("005930");string fault="";processor.Faulted+=value=>fault=value;
            processor.Process("SC",utc);Check(processor.Failed&&!fault.Contains("SECRET")&&!fault.Contains("12345678901")&&!fault.Contains("1111"),"provider exceptions latch the channel without exposing account values");
            processor.Process("SC",utc);Check(!processor.TryGetQuote("005930",utc,out ready),"failed channel cannot silently resume using cached data");processor.Dispose();
            processor=new RealtimeEventProcessor(index=>Field(fields,index));processor.Process(42,utc);Check(processor.Failed,"non-string VARIANT realtime type is refused rather than coerced");processor.Dispose();
            return checks;
        }
    }
}
