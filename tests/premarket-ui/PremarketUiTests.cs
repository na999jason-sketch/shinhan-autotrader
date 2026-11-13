using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Runs the actual form and read-only coordinator against synthetic stock/bar
// data. No Connect, AttachAccount, Arm, realtime constructor or order wire runs.
static class PremarketUiTests {
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly StringBuilder Results=new StringBuilder();
    static readonly DateTime Opening=new DateTime(2026,11,16,0,0,0,DateTimeKind.Utc);
    static readonly DateTime BeforeOpen=Opening.AddMinutes(-30);
    static int assertions;
    static void Check(bool value,string label) {
        assertions++;if(!value)throw new InvalidOperationException(label);
        Results.AppendLine("PASS "+label);
    }
    static T Get<T>(object owner,string field) {return (T)owner.GetType().GetField(field,Hidden).GetValue(owner);}
    static void Set(object owner,string field,object value) {owner.GetType().GetField(field,Hidden).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] arguments) {
        try{return owner.GetType().GetMethod(name,Hidden).Invoke(owner,arguments);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    static MainForm NewForm() {
        var form=new MainForm();Get<Timer>(form,"scheduler").Stop();
        form.ShowInTaskbar=false;form.Show();Application.DoEvents();Get<Timer>(form,"scheduler").Stop();return form;
    }
    static Dictionary<string,PremarketReference> Prepared(MainForm form) {return Get<Dictionary<string,PremarketReference>>(form,"preparedReferences");}
    static void Schedule(MainForm form,DateTime now) {Call(form,"ScheduleAt",now);}
    static void Close(MainForm form,string data) {
        form.Close();
        using(var file=new FileStream(Path.Combine(data,"live-settings-v1.xml.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Check(file.CanWrite,"window close releases its settings lease");
    }
    static void Bind(MainForm form) {
        Get<NumericUpDown>(form,"budget").Value=1000000;
        Call(form,"SaveSettings");
        var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
        var credentials=Activator.CreateInstance(type,new object[]{"12345678901","1111"});
        var key=(string)type.GetProperty("Key").GetValue(credentials,null);
        Set(form,"credentials",credentials);Set(form,"boundKey",key);Set(form,"lastReportKey",key);
        Get<LiveTradingSession>(form,"authorization").Authorize("12345678901",BeforeOpen);
        Get<UnattendedSession>(form,"session").Arm(BeforeOpen);
        Set(form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
        Set(form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
    }

    sealed class SyntheticTransport : IIndiTransport {
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public readonly List<string> Queries=new List<string>();
        public readonly List<Bar> Bars=new List<Bar>();
        public readonly string[] Codes={"000660","005930","035420"};
        readonly Dictionary<int,string> inputs=new Dictionary<int,string>();
        string query;int nextId,pending;
        public SyntheticTransport() {
            var dates=new List<DateTime>();var date=Opening.Date.AddDays(-1);
            while(dates.Count<45) {
                if(date.DayOfWeek!=DayOfWeek.Saturday&&date.DayOfWeek!=DayOfWeek.Sunday)dates.Add(date);
                date=date.AddDays(-1);
            }
            dates.Reverse();
            for(int i=0;i<dates.Count;i++) {
                decimal close=7000+i*10;
                Bars.Add(new Bar{Date=dates[i].ToString("yyyyMMdd",CultureInfo.InvariantCulture),Open=close,High=close+10,Low=close-10,Close=close,Volume=1000});
            }
        }
        public object Invoke(string method,params object[] arguments) {
            if(method=="SetQueryName"){query=(string)arguments[0];inputs.Clear();return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData"){inputs[(int)arguments[0]]=(string)arguments[1];return true;}
            if(method=="RequestData") {
                if(query!="stock_mst"&&query!="TR_SCHART")throw new InvalidOperationException("Unexpected non-research request: "+query);
                Queries.Add(query);pending=++nextId;return (short)pending;
            }
            if(method=="GetMultiRowCount")return query=="stock_mst"?Codes.Length:Bars.Count;
            if(method=="GetMultiData") {
                int row=(int)arguments[0],column=(int)arguments[1];
                if(query=="stock_mst") {
                    if(column==1)return Codes[row];if(column==2||column==6||column==7||column==8)return "0";
                    if(column==3)return "SYNTHETIC "+row;if(column==14)return "ST";return "";
                }
                var bar=Bars[row];
                if(column==0)return bar.Date;
                decimal number=column==2?bar.Open:column==3?bar.High:column==4?bar.Low:column==5?bar.Close:column==9?bar.Volume:0;
                return number.ToString(CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("Unexpected synthetic provider method: "+method);
        }
        public void Complete() {
            if(pending==0)throw new InvalidOperationException("No synthetic response is pending.");
            int id=pending;pending=0;if(ReceiveData!=null)ReceiveData(id);
        }
        public void Dispose(){}
    }
    sealed class Fixture {
        public readonly SyntheticTransport Wire=new SyntheticTransport();
        public readonly IndiApi Api;
        public Fixture(MainForm form) {
            Api=new IndiApi(Get<Panel>(form,"nativeArea"));
            var requests=new IndiRequestCoordinator(Wire);
            requests.Fatal+=message=>{throw new InvalidOperationException("Synthetic coordinator failed: "+message);};
            requests.Done+=reply=>{Get<Timer>(Api,"timeout").Stop();Call(form,"Reply",reply);};
            Set(Api,"requests",requests);Set(Api,"hasReceivedData",true);Set(form,"api",Api);
            if(!Api.Stocks())throw new InvalidOperationException("Synthetic stock request did not start.");Wire.Complete();
        }
    }
    static void Begin(MainForm form) {Call(form,"BeginResearch",BeforeOpen,false);}
    static void FinishPreparation(MainForm form,Fixture fixture,int completed) {
        for(int i=completed;i<fixture.Wire.Codes.Length;i++) {
            Schedule(form,BeforeOpen.AddSeconds(i+1));
            Check(fixture.Api.Busy,"research scheduler issues the next synthetic daily-bar request");
            fixture.Wire.Complete();
        }
        Schedule(form,BeforeOpen.AddSeconds(10));
    }
    static void CheckNoOrderTransport(MainForm form,string label) {
        Check(Get<RealtimeIndi>(form,"realtime")==null&&Get<LiveOrderGateway>(form,"gateway")==null
            &&Get<LiveOrderJournal>(form,"journal")==null&&Get<object>(form,"entry")==null,label);
    }
    static void Capture(MainForm form,string path) {
        foreach(var tabs in All(form).OfType<TabControl>())
            tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(page=>page.Text=="발굴 후보");
        form.PerformLayout();Application.DoEvents();form.Refresh();Application.DoEvents();
        using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(form.PointToScreen(System.Drawing.Point.Empty),System.Drawing.Point.Empty,bitmap.Size);
            bitmap.Save(path);
        }
    }
    static IEnumerable<Control> All(Control owner) {
        foreach(Control child in owner.Controls){yield return child;foreach(var nested in All(child))yield return nested;}
    }

    static void RealtimeStatus(string data) {
        using(var form=NewForm()) {
            var fields=new Dictionary<int,object>{{1,"005930"},{2,"09:00:00"},{3,"70000"},{7,"100"},{17,"1"}};
            var processor=new RealtimeEventProcessor(index=>fields[index]);processor.Track("005930");
            // Deliberately bypass the COM constructor: only the in-memory
            // processor and status labels run. No OCX or registration API exists.
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){"005930"});
            Set(form,"realtime",realtime);Get<HashSet<string>>(form,"trackedQuotes").Add("005930");
            Call(form,"EvaluatePreparedSignals",Opening);
            string status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 0 / 등록 1")&&status.Contains("콜백 없음")&&Get<Label>(form,"quoteStatus").ForeColor==System.Drawing.Color.DarkRed,
                "registered quotes with zero callbacks are identified in red rather than described as an entry-signal wait");
            processor.Process("SC",Opening);
            Call(form,"EvaluatePreparedSignals",Opening);
            Check(Get<Label>(form,"quoteStatus").Text.Contains("확인 0 / 등록 1"),"one actual callback still waits for sequence progression");
            fields[2]="09:00:01";fields[7]="101";processor.Process("SC",Opening.AddSeconds(1));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(1));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 1 / 등록 1")&&status.Contains("정상")&&Get<Label>(form,"quoteStatus").ForeColor==System.Drawing.Color.Black,
                "advancing colon-formatted exchange clocks establish a confirmed realtime quote in the actual form status");
            fields[2]="09000212";fields[7]="103";processor.Process("SC",Opening.AddSeconds(2));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(2));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 1 / 등록 1")&&status.Contains("정상")&&status.Contains("09000212")&&status.Contains("digits8"),
                "eight numeric HHmmssff exchange clocks retain bounded raw diagnostics and continue the confirmed quote sequence");
            fields[2]="00090003";fields[7]="104";processor.Process("SC",Opening.AddSeconds(3));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(3));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 1 / 등록 1")&&status.Contains("정상")&&status.Contains("00090003"),
                "zero-padded 00HHmmss exchange clocks remain usable through explicit regular-session parsing");
            fields[2]="09000499";fields[7]="105";processor.Process("SC",Opening.AddSeconds(100));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(100));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 0 / 등록 1")&&status.Contains("시각 불일치")&&status.Contains("09000499"),
                "valid eight-digit syntax with a stale exchange time remains unconfirmed for trading and reports clock mismatch");
            processor.Invalidate();
            fields[2]="09014112";fields[7]="106";processor.Process("SC",Opening.AddSeconds(101));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(101));
            Check(Get<Label>(form,"quoteStatus").Text.Contains("확인 0 / 등록 1"),
                "one fresh eight-digit quote after explicit source invalidation does not bypass sequence confirmation");
            fields[2]="09014212";fields[7]="107";processor.Process("SC",Opening.AddSeconds(102));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(102));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 1 / 등록 1")&&status.Contains("정상"),
                "a second advancing fresh eight-digit quote restores confirmed form status after source invalidation");
            fields[2]="99619999";fields[7]="108";processor.Process("SC",Opening.AddSeconds(103));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(103));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 0 / 등록 1")&&status.Contains("시각 형식")&&status.Contains("99619999"),
                "eight digits alone cannot establish a clock when neither explicit format is valid");
            fields[2]="unrecognized";processor.Process("SC",Opening.AddSeconds(104));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(104));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 0 / 등록 1")&&status.Contains("시각 형식"),"received SC with an invalid clock is distinguished from missing callbacks and cannot remain trade-ready");
            Check(!processor.Failed&&Get<LiveOrderGateway>(form,"gateway")==null&&Get<LiveOrderJournal>(form,"journal")==null
                &&Get<object>(form,"entry")==null&&!Get<LiveTradingSession>(form,"authorization").Active,"realtime status regression never constructs an order transport or authorizes a live order");
            Close(form,data);
        }
    }

    static BrokerHoldingsSnapshot CashProfileSnapshot(bool ordinary,bool verified=true) {
        var now=DateTime.UtcNow;
        var source=new BrokerHoldingsSnapshot{RequestStartedUtc=now.AddMilliseconds(-10),ReceivedUtc=now,
            BasisVerified=!ordinary,RequestBasisVerified=!ordinary,RequestOrdinaryOnlyVerified=ordinary&&verified};
        source.Holdings.Add(new Holding{Code="069500",Name="SYNTHETIC ETF A",Quantity=2,AvgPrice=140940,Price=ordinary?100:86295});
        source.Holdings.Add(new Holding{Code="305720",Name="SYNTHETIC ETF B",Quantity=8,AvgPrice=14350,Price=ordinary?200:13205});
        foreach(var held in source.Holdings) {
            source.BasisValidated[held.Code]=!ordinary;
            source.BasisDiagnostics[held.Code]=ordinary?"OrdinaryBalanceCode5":"Blank";
            source.CashSourceValidated[held.Code]=false;
            source.CashProfileCompatible[held.Code]=true;
            source.CashProfileDiagnostics[held.Code]="Kind=Numeric1;Loan=Blank";
            source.SellableQuantities[held.Code]=(int)held.Quantity;source.UnfilledQuantities[held.Code]=0;
        }
        return source;
    }
    static void OrdinaryReply(MainForm form,BrokerHoldingsSnapshot source) {
        Call(form,"Reply",new IndiReply{Kind="ORDINARYHOLDINGS",TradeHoldings=source,
            RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});
    }
    static string SellDecision(MainForm form,Holding held) {
        var utc=Opening.AddSeconds(1);
        // The strategy uses a deterministic exchange clock. Restore the real
        // receipt stamps before testing the actual form's current-time display.
        var stamps=new[]{Get<BrokerHoldingsSnapshot>(form,"tradeHoldings"),Get<BrokerHoldingsSnapshot>(form,"ordinaryHoldings")}
            .Where(source=>source!=null).Select(source=>Tuple.Create(source,source.RequestStartedUtc,source.ReceivedUtc)).ToList();
        foreach(var stamp in stamps){stamp.Item1.RequestStartedUtc=utc.AddMilliseconds(-10);stamp.Item1.ReceivedUtc=utc;}
        try {
            return (string)Call(form,"EvaluateHoldingSell",held,
                new IndiReply{Code=held.Code,Price=held.Price,Clock="090001",HasTradeClock=true},utc);
        }finally {
            foreach(var stamp in stamps){stamp.Item1.RequestStartedUtc=stamp.Item2;stamp.Item1.ReceivedUtc=stamp.Item3;}
        }
    }
    static void OrdinarySourceStatus(string data) {
        using(var form=NewForm()) {
            Bind(form);
            Check(Get<UnattendedSession>(form,"session").Tick(Opening,false)==UnattendedAction.StartScan,
                "cash-source UI fixture opens only the in-memory session without scheduling a request");
            var trade=CashProfileSnapshot(false);
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=trade,
                RequestStartedUtc=trade.RequestStartedUtc,ReceivedUtc=trade.ReceivedUtc});
            var held=Get<List<Holding>>(form,"holdings");
            Check(held.Count==2&&held.All(x=>SellDecision(form,x)==null&&(string)Call(form,"HoldingDecision",x)=="현물 잔고구분 확인 대기"),
                "unknown cash metadata initially leaves both existing holdings waiting for an ordinary-position source");

            var missing=CashProfileSnapshot(true);missing.Holdings.Clear();OrdinaryReply(form,missing);
            Check(held.All(x=>SellDecision(form,x)==null&&(string)Call(form,"HoldingDecision",x)=="현물 잔고구분 확인 대기"),
                "an ordinary-only response missing the holdings cannot confirm their cash source");
            var mismatch=CashProfileSnapshot(true);mismatch.Holdings[0].Quantity=1;mismatch.SellableQuantities["069500"]=1;mismatch.Holdings.RemoveAt(1);
            OrdinaryReply(form,mismatch);
            Check(held.All(x=>SellDecision(form,x)==null&&(string)Call(form,"HoldingDecision",x)=="현물 잔고구분 확인 대기"),
                "mismatched ordinary quantities or a missing symbol keep the original holdings blocked");
            OrdinaryReply(form,CashProfileSnapshot(true,false));
            Check(held.All(x=>SellDecision(form,x)==null&&(string)Call(form,"HoldingDecision",x)=="현물 잔고구분 확인 대기"),
                "matching ordinary rows without verified request provenance do not confirm a cash position");

            OrdinaryReply(form,CashProfileSnapshot(true));
            Check(held.All(x=>!string.IsNullOrEmpty(SellDecision(form,x))&&((string)Call(form,"HoldingDecision",x)).Contains("손절 -6")),
                "verified ordinary-only selection lets both existing holdings reach their configured six-percent stop-loss decision");
            Call(form,"RefreshAccount");
            Check(ReferenceEquals(Get<BrokerHoldingsSnapshot>(form,"tradeHoldings"),trade)
                &&held[0].Quantity==2&&held[0].AvgPrice==140940&&held[0].Price==86295
                &&held[1].Quantity==8&&held[1].AvgPrice==14350&&held[1].Price==13205,
                "ordinary selection verifies classification without replacing original trade quantities cost or market prices");
            var grid=Get<DataGridView>(form,"holdingsGrid");
            Check(grid.Rows.Count==2&&grid.Rows.Cast<DataGridViewRow>().All(row=>Convert.ToString(row.Cells["매도판정"].Value,CultureInfo.InvariantCulture).Contains("손절 -6"))
                &&Convert.ToString(grid.Rows[0].Cells["평균단가"].Value,CultureInfo.InvariantCulture)=="140,940"
                &&Convert.ToString(grid.Rows[0].Cells["현재가"].Value,CultureInfo.InvariantCulture)=="86,295",
                "actual holdings grid displays the stop decisions with unchanged original prices");
            Check(Get<IndiApi>(form,"api")==null&&Get<RealtimeIndi>(form,"realtime")==null&&Get<LiveOrderGateway>(form,"gateway")==null
                &&Get<LiveOrderJournal>(form,"journal")==null&&Get<object>(form,"entry")==null,
                "cash-source decision regression activates no broker realtime or order transport");
            Close(form,data);
        }
    }
    static void CandidateStatus(MainForm form,PremarketReference reference) {
        var fields=new Dictionary<int,object>{{1,reference.Code},{2,"090000"},{3,reference.PriorHigh.ToString(CultureInfo.InvariantCulture)},{7,"100"},{17,"1"}};
        var processor=new RealtimeEventProcessor(index=>fields[index]);processor.Track(reference.Code);
        var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
        Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){reference.Code});
        Set(form,"realtime",realtime);Get<HashSet<string>>(form,"trackedQuotes").Add(reference.Code);
        var row=Get<DataGridView>(form,"candidatesGrid").Rows.Cast<DataGridViewRow>()
            .Single(item=>Convert.ToString(item.Cells["코드"].Value,CultureInfo.InvariantCulture)==reference.Code);
        try {
            processor.Process("SC",Opening);fields[2]="090001";fields[7]="101";processor.Process("SC",Opening.AddSeconds(1));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(1));
            Check(Convert.ToString(row.Cells["주문상태"].Value,CultureInfo.InvariantCulture).Contains("미돌파"),
                "confirmed live quotes update the actual candidate row with its unmet price-breakout condition");
            fields[2]="090002";fields[3]=(reference.PriorHigh+10).ToString(CultureInfo.InvariantCulture);fields[7]="102";processor.Process("SC",Opening.AddSeconds(2));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(2));
            Check(Convert.ToString(row.Cells["주문상태"].Value,CultureInfo.InvariantCulture).Contains("누적거래량")
                &&Convert.ToString(row.Cells["주문상태"].Value,CultureInfo.InvariantCulture).Contains("미달"),
                "candidate row distinguishes a sufficient breakout price from insufficient live cumulative volume");
            fields[2]="090003";fields[7]=(reference.AverageVolume*2).ToString(CultureInfo.InvariantCulture);processor.Process("SC",Opening.AddSeconds(3));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(3));
            Check(Convert.ToString(row.Cells["주문상태"].Value,CultureInfo.InvariantCulture).Contains("매수 조건 충족"),
                "candidate row identifies satisfied live price and volume conditions before account preflight");
            Check(Get<Queue<Candidate>>(form,"buyQuotes").Count==0&&Get<LiveOrderGateway>(form,"gateway")==null&&Get<LiveOrderJournal>(form,"journal")==null
                &&Get<object>(form,"entry")==null,"candidate status changes cannot queue or transmit an order without an order journal");
        }finally {Call(form,"DisposeRealtime");}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Sends,Preparations;
        public Action DuringPreparation;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;if(DuringPreparation!=null)DuringPreparation();}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("This UI fixture must never transmit even a synthetic order.");
        }
        public void Dispose(){}
    }
    static LiveOrderRecord RejectedSell(LiveOrderJournal journal,string code,string key,int requestId,bool evidence) {
        var utc=Opening.AddSeconds(1);
        var order=journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=code,Quantity=code=="069500"?2:8,
            LimitPrice=code=="069500"?86295:13205,ClientKey=key},utc);
        journal.MarkDispatching(order.Id,utc);journal.MarkRequestAccepted(order.Id,requestId,utc);
        OrderResponseEvidence response=null;
        if(evidence) {
            var fields=new[]{"0","0","0","0","1","수량부족 계좌12345678901 암호1111","0","0"};
            response=LiveOrderResponseParser.Read(requestId,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=1},
                OrderResponseLayout.Modern,new[]{"12345678901","1111"});
        }
        journal.RecordAcknowledgement(order.Id,requestId,"0",true,utc,response);
        return journal.Snapshot.Orders.Single(item=>item.Id==order.Id);
    }
    static void RejectedOrderStatus(string data) {
        using(var form=NewForm()) {
            var fixture=new Fixture(form);Bind(form);Get<UnattendedSession>(form,"session").Tick(Opening,false);
            using(var journal=new LiveOrderJournal(Path.Combine(data,"ui-rejection-journal.xml"))) {
                journal.BindAccount("12345678901");Set(form,"journal",journal);
                var legacy=RejectedSell(journal,"069500","legacy-rejected",11,false);
                var detailed=RejectedSell(journal,"305720","detailed-rejected",12,true);
                var identified=journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="035420",Quantity=1,LimitPrice=70000,ClientKey="identified-rejected"},Opening.AddSeconds(1));
                journal.MarkDispatching(identified.Id,Opening.AddSeconds(1));journal.MarkRequestAccepted(identified.Id,13,Opening.AddSeconds(1));
                journal.RecordAcknowledgement(identified.Id,13,"777",false,Opening.AddSeconds(1));
                journal.RecordBrokerRejection("777",MarketTime.ToKorea(Opening).ToString("yyyyMMdd",CultureInfo.InvariantCulture),"09",Opening.AddSeconds(1));
                var unknown=journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="005930",Quantity=1,LimitPrice=70000,ClientKey="unknown-order"},Opening.AddSeconds(1));
                journal.MarkUnknown(unknown.Id,Opening.AddSeconds(1));
                var wire=new NoOrderWire();Set(form,"gateway",new LiveOrderGateway(journal,wire));
                Set(form,"readiness",new BrokerReadiness(Get<string>(form,"boundKey"),journal.Snapshot.AccountFingerprint));
                Call(form,"RefreshOrders");var grid=Get<DataGridView>(form,"ordersGrid");
                Check(grid.Columns.Count==10&&grid.Columns.Contains("응답 사유"),"order grid exposes a separate response-reason column");
                var rows=grid.Rows.Cast<DataGridViewRow>().ToDictionary(row=>((LiveOrderRecord)row.Tag).Id,StringComparer.Ordinal);
                Check(Convert.ToString(rows[legacy.Id].Cells["주문번호"].Value,CultureInfo.InvariantCulture)=="접수 거절"
                    &&Convert.ToString(rows[legacy.Id].Cells["미체결"].Value,CultureInfo.InvariantCulture)=="해당 없음",
                    "a local rejection without a broker order number is not displayed as a working unfilled order");
                Check(Convert.ToString(rows[legacy.Id].Cells["응답 사유"].Value,CultureInfo.InvariantCulture)=="이전 버전: 상세 응답 미기록",
                    "historical rejected records explicitly identify absent response diagnostics");
                string reason=Convert.ToString(rows[detailed.Id].Cells["응답 사유"].Value,CultureInfo.InvariantCulture);
                Check(reason.Contains("수량부족")&&!reason.Contains("12345678901")&&!reason.Contains("1111"),
                    "actual order row displays the stored rejection explanation with credentials redacted");
                grid.ClearSelection();grid.CurrentCell=rows[detailed.Id].Cells[0];rows[detailed.Id].Selected=true;
                var details=Get<TextBox>(form,"orderResponseDetails");
                Check(details.ReadOnly&&details.Multiline&&details.Text==reason&&details.Text.Contains("수량부족"),
                    "selecting a rejection exposes its complete sanitized explanation in the read-only details area");
                Check(Convert.ToString(rows[identified.Id].Cells["주문번호"].Value,CultureInfo.InvariantCulture)=="777"
                    &&Convert.ToString(rows[identified.Id].Cells["확인상태"].Value,CultureInfo.InvariantCulture)=="거절",
                    "a later rejection preserves an already identified broker order number");
                Check(Convert.ToString(rows[unknown.Id].Cells["주문번호"].Value,CultureInfo.InvariantCulture)!="접수 거절"
                    &&Convert.ToString(rows[unknown.Id].Cells["확인상태"].Value,CultureInfo.InvariantCulture)=="결과 불명",
                    "unknown order evidence remains visually distinct from a definite rejection");
                grid.ClearSelection();grid.CurrentCell=rows[unknown.Id].Cells[0];rows[unknown.Id].Selected=true;
                bool refused=false;try{Call(form,"RetrySelectedRejectedSellAt",Opening.AddSeconds(1));}catch(InvalidOperationException){refused=true;}
                Check(refused&&Get<object>(form,"entry")==null&&wire.Sends==0&&wire.Preparations==0&&journal.Snapshot.Orders.Count==4,
                    "selecting an unknown order cannot retry it or create a new journal record");
                Check(fixture.Wire.Queries.Count==1,"displaying and refusing uncertain order retries requests no additional broker query");
                Close(form,data);
            }
        }
    }
    static void AcceptedUnknownStatus(string data) {
        using(var form=NewForm()) {
            var fixture=new Fixture(form);Bind(form);
            using(var journal=new LiveOrderJournal(Path.Combine(data,"ui-accepted-unknown-journal.xml"))) {
                journal.BindAccount("12345678901");Set(form,"journal",journal);
                var historical=RejectedSell(journal,"305720","historical-rejection",31,false);
                var accepted=journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="069500",Quantity=2,LimitPrice=83775,ClientKey="accepted-reconciliation"},Opening.AddSeconds(2));
                journal.MarkDispatching(accepted.Id,Opening.AddSeconds(2));journal.MarkRequestAccepted(accepted.Id,32,Opening.AddSeconds(2));
                var fields=new[]{"0","000000000000812345","0","","1","총주문금액 :167,550","가계산수수료 :830","가계산제세금 :0"};
                var evidence=LiveOrderResponseParser.Read(32,index=>fields[index],new IndiDiagnosticSnapshot{CommState=0,ErrorState=0},OrderResponseLayout.Modern,new string[0]);
                journal.RecordAcknowledgement(accepted.Id,32,evidence.BrokerOrderNumber,false,Opening.AddSeconds(2),evidence);
                Call(form,"RefreshOrders");var grid=Get<DataGridView>(form,"ordersGrid");
                var historicalRow=grid.Rows.Cast<DataGridViewRow>().Single(row=>(row.Tag as LiveOrderRecord).Id==historical.Id);
                grid.ClearSelection();grid.CurrentCell=historicalRow.Cells[0];historicalRow.Selected=true;
                Call(form,"OnGatewayOrderChanged",journal.Snapshot.Orders.Single(order=>order.Id==accepted.Id));
                Check((grid.SelectedRows[0].Tag as LiveOrderRecord).Id==accepted.Id&&Get<TextBox>(form,"orderResponseDetails").Text.Contains("result=Accepted"),
                    "a newly accepted response selects its own row and details instead of an older rejection");
                journal.MarkUnknown(accepted.Id,Opening.AddSeconds(3));Call(form,"RefreshOrders");
                var current=grid.Rows.Cast<DataGridViewRow>().Single(row=>(row.Tag as LiveOrderRecord).Id==accepted.Id);
                Check(Convert.ToString(current.Cells["확인상태"].Value,CultureInfo.InvariantCulture)=="접수 확인 · 체결 대사 필요"
                    &&Get<TextBox>(form,"orderResponseDetails").Text.Contains("접수 응답과 주문번호 확인")
                    &&journal.RecoveryRequired&&journal.Snapshot.Orders.Single(order=>order.Id==accepted.Id).Status==LiveOrderStatus.Unknown,
                    "accepted broker identity remains visible while durable Unknown and recovery restrictions remain unchanged");
                historicalRow=grid.Rows.Cast<DataGridViewRow>().Single(row=>(row.Tag as LiveOrderRecord).Id==historical.Id);
                grid.ClearSelection();grid.CurrentCell=historicalRow.Cells[0];historicalRow.Selected=true;Call(form,"RefreshOrders");
                Check((grid.SelectedRows[0].Tag as LiveOrderRecord).Id==historical.Id&&Get<TextBox>(form,"orderResponseDetails").Text=="이전 버전: 상세 응답 미기록",
                    "periodic order refresh preserves the user's selected historical record");
                var brokerSnapshot=new BrokerOrdersSnapshot{TradingDate=MarketTime.ToKorea(Opening).ToString("yyyyMMdd",CultureInfo.InvariantCulture),ReceivedUtc=Opening.AddSeconds(4)};
                var brokerRow=new BrokerOrderRow{OrderNumber="812345",Code="069500",SideCode="1",Quantity=2,FilledQuantity=0,OpenQuantity=2,LimitPrice=83775};
                brokerSnapshot.Orders.Add(brokerRow);Set(form,"brokerOrders",brokerSnapshot);
                Set(form,"preferredOrderSelectionKey","broker:"+brokerSnapshot.TradingDate+":069500:812345");Call(form,"RefreshOrders");
                Check(grid.Rows.Count==2&&ReferenceEquals(grid.SelectedRows[0].Tag,brokerRow)
                    &&Convert.ToString(grid.SelectedRows[0].Cells["확인상태"].Value,CultureInfo.InvariantCulture)=="접수 확인 · 체결 대사 필요"
                    &&Get<TextBox>(form,"orderResponseDetails").Text.Contains("result=Accepted"),
                    "unpadded broker snapshot merges its padded ACK identity without duplicates and retains response evidence and broker-row actions");
                brokerRow.ConfirmationCode="1";Call(form,"RefreshOrders");
                Check(Convert.ToString(grid.SelectedRows[0].Cells["확인상태"].Value,CultureInfo.InvariantCulture)=="증권사 거부 · 대사 필요"
                    &&Get<TextBox>(form,"orderResponseDetails").Text.Contains("result=Accepted")&&journal.RecoveryRequired,
                    "a later broker rejection takes display priority while preserving earlier acceptance evidence and recovery requirements");
                Check(fixture.Wire.Queries.Count==1,"accepted reconciliation presentation sends no broker query or order");
                Close(form,data);
            }
        }
    }
    static void RejectedSellQueue(string data) {
        using(var form=NewForm()) {
            var fixture=new Fixture(form);Bind(form);Get<UnattendedSession>(form,"session").Tick(Opening,false);
            using(var journal=new LiveOrderJournal(Path.Combine(data,"ui-retry-journal.xml"))) {
                journal.BindAccount("12345678901");Set(form,"journal",journal);
                var previous=RejectedSell(journal,"069500","original-rejected-sell",21,true);
                var wire=new NoOrderWire();Set(form,"gateway",new LiveOrderGateway(journal,wire));
                Check(wire.Preparations==0&&wire.Sends==0,"constructing the order gateway does not initialize or send through its order channel");
                Set(form,"readiness",new BrokerReadiness(Get<string>(form,"boundKey"),journal.Snapshot.AccountFingerprint));
                var trade=CashProfileSnapshot(false);var ordinary=CashProfileSnapshot(true);var utc=Opening.AddSeconds(1);
                foreach(var source in new[]{trade,ordinary}){source.RequestStartedUtc=utc.AddMilliseconds(-10);source.ReceivedUtc=utc;}
                Set(form,"tradeHoldings",trade);Set(form,"ordinaryHoldings",ordinary);Get<List<Holding>>(form,"holdings").AddRange(trade.Holdings);
                Get<Dictionary<string,CashInstrument>>(form,"instruments")["069500"]=new CashInstrument{Code="069500",Market="0",Product="EF"};
                var fields=new Dictionary<int,object>{{1,"069500"},{2,"090000"},{3,"86295"},{7,"100"},{17,"1"}};
                var processor=new RealtimeEventProcessor(index=>fields[index]);processor.Track("069500");processor.Process("SC",Opening);
                fields[2]="090001";fields[7]="101";processor.Process("SC",utc);
                var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
                Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){"069500"});Set(realtime,"native",new object());
                Set(form,"realtime",realtime);Get<HashSet<string>>(form,"trackedQuotes").Add("069500");
                Call(form,"RefreshOrders");var grid=Get<DataGridView>(form,"ordersGrid");grid.ClearSelection();grid.CurrentCell=grid.Rows[0].Cells[0];grid.Rows[0].Selected=true;
                Set(form,"monitoring",true);
                wire.DuringPreparation=()=>{
                    wire.DuringPreparation=null;
                    Check(Get<bool>(form,"orderPreparing")&&Get<object>(form,"entry")==null,
                        "order initialization claims its guard before a fake StartIndi can pump callbacks");
                    Schedule(form,utc);
                    Check(wire.Preparations==1&&Get<object>(form,"entry")==null&&fixture.Wire.Queries.Count==1,
                        "a scheduler tick pumped during initialization cannot prepare a second order or issue a TR");
                    bool duplicateRefused=false;try{Call(form,"RetrySelectedRejectedSellAt",utc);}catch(InvalidOperationException){duplicateRefused=true;}
                    Check(duplicateRefused&&wire.Preparations==1&&wire.Sends==0&&Get<LiveTradingSession>(form,"authorization").Active,
                        "a retry click pumped during preparation is refused without revoking the original authorized operation");
                };
                Call(form,"RetrySelectedRejectedSellAt",utc);
                var pending=Get<object>(form,"entry");
                var intent=pending==null?null:(LiveOrderIntent)pending.GetType().GetField("Intent").GetValue(pending);
                Check(intent!=null&&intent.ClientKey.StartsWith("retry-"+previous.Id+"-",StringComparison.Ordinal)
                    &&intent.ClientKey!=previous.ClientKey&&intent.Kind==LiveOrderKind.Sell&&intent.Code=="069500"&&intent.Quantity==2&&intent.LimitPrice==86295,
                    "selected definite rejection creates a distinct retry intent using current holdings and confirmed quote");
                var reads=(Queue<string>)pending.GetType().GetField("Reads").GetValue(pending);
                Check(new[]{"BARS","ORDINARYHOLDINGS","TRADEHOLDINGS","CASH","ORDERS"}.All(reads.Contains),
                    "manual retry queues fresh price account classification cash and broker-order preflight instead of reusing old approval evidence");
                Check(journal.Snapshot.Orders.Count==1&&journal.Snapshot.Orders.Single().Id==previous.Id
                    &&journal.Snapshot.Orders.Single().Status==LiveOrderStatus.Rejected&&wire.Sends==0&&wire.Preparations==1&&fixture.Wire.Queries.Count==1,
                    "queueing a manual retry preserves rejected audit history and transmits no order or additional query");
                Check(!Get<bool>(form,"orderPreparing")&&Get<Label>(form,"orderGate").Text.Contains("새 주문 069500 / 사전검증 대기"),
                    "successful initialization releases its guard and visibly identifies a new pending intent");
                bool refused=false;try{Call(form,"RetrySelectedRejectedSellAt",utc);}catch(InvalidOperationException){refused=true;}
                Check(refused&&ReferenceEquals(Get<object>(form,"entry"),pending)&&wire.Sends==0&&wire.Preparations==1,
                    "repeated retry clicks cannot replace an already queued preflight or transmit an order");
                Call(form,"RunPreflight",utc.AddMilliseconds(2));
                Check(Get<Label>(form,"orderGate").Text.Contains("새 주문 069500 / 완료봉 조회 응답 대기")&&fixture.Api.Busy,
                    "fresh selected retry displays its actual preflight read stage before any transmission");
                fixture.Wire.Complete();Set(form,"entry",null);
                wire.DuringPreparation=()=>Get<LiveTradingSession>(form,"authorization").Revoke();
                Call(form,"RetrySelectedRejectedSellAt",utc);
                Check(!Get<bool>(form,"orderPreparing")&&Get<object>(form,"entry")==null&&wire.Sends==0
                    &&Get<Label>(form,"state").Text.Contains("새 주문 준비 중단"),
                    "authorization revoked during channel initialization cannot leave a queued order and reports why preparation stopped");
                Get<LiveTradingSession>(form,"authorization").Authorize("12345678901",BeforeOpen);
                wire.DuringPreparation=()=>{throw new InvalidOperationException("Synthetic initialization failure");};
                bool initializationFailed=false;try{Call(form,"RetrySelectedRejectedSellAt",utc);}catch(InvalidOperationException){initializationFailed=true;}
                Check(initializationFailed&&!Get<bool>(form,"orderPreparing")&&!Get<LiveTradingSession>(form,"authorization").Active
                    &&Get<object>(form,"entry")==null&&wire.Sends==0&&journal.Snapshot.Orders.Count==1,
                    "channel initialization failure releases the guard revokes authorization and preserves all prior audit records");
                Close(form,data);
            }
        }
    }
    sealed class DeferredReadTransport : IIndiTransport,IIndiDiagnostics {
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public int Requests,SetCalls;bool completed;string query;
        readonly Dictionary<int,string> inputs=new Dictionary<int,string>();
        public readonly List<Dictionary<int,string>> SubmittedInputs=new List<Dictionary<int,string>>();
        public IndiDiagnosticSnapshot ReadDiagnostics(){return new IndiDiagnosticSnapshot{ErrorState=completed?0:Requests==1?1:Requests==0?0:2,ErrorCode=Requests==1?"GI004":""};}
        public object Invoke(string method,params object[] arguments) {
            if(method=="SetQueryName"){query=(string)arguments[0];inputs.Clear();if(query!="SABA609Q1")throw new InvalidOperationException("Only a synthetic read-only balance query is supported.");return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData"){SetCalls++;inputs[(int)arguments[0]]=(string)arguments[1];return true;}
            if(method=="RequestData"){SubmittedInputs.Add(new Dictionary<int,string>(inputs));Requests++;return Requests==1?(short)0:(short)41;}
            if(method=="GetMultiRowCount")return 0;
            if(method=="GetSingleData")return (int)arguments[0]==0?MarketTime.NowKorea.ToString("yyyyMMdd",CultureInfo.InvariantCulture):"";
            throw new InvalidOperationException("Unsupported synthetic read-only provider method: "+method);
        }
        public void Complete(){completed=true;if(ReceiveData!=null)ReceiveData(41);}
        public void Dispose(){}
    }
    static void DeferredReadStatus(string data) {
        using(var form=NewForm()) {
            var wire=new DeferredReadTransport();var requests=new IndiRequestCoordinator(wire);
            var api=new IndiApi(Get<Panel>(form,"nativeArea"));Set(api,"requests",requests);Set(api,"hasReceivedData",true);Set(form,"api",api);
            int received=0;requests.Fatal+=message=>{throw new InvalidOperationException("Synthetic read-only retry failed: "+message);};
            requests.Done+=reply=>{received++;Get<Timer>(api,"timeout").Stop();Call(form,"Reply",reply);};
            var before=DateTime.UtcNow;
            Check(api.TradeHoldings("12345678901","1111")&&api.Busy&&wire.Requests==1&&received==0,
                "an unaccepted GI004 balance read remains pending without inventing a holdings response");
            Schedule(form,before.AddMilliseconds(200));
            Check(wire.Requests==1&&api.Busy&&received==0,"actual form scheduler respects the read-only retry delay while the API is busy");
            Schedule(form,before.AddSeconds(2));
            Check(wire.Requests==2&&api.Busy&&received==0,"actual form scheduler polls and dispatches the deferred read before its busy-API return");
            Check(wire.SubmittedInputs.Count==2&&wire.SetCalls==16&&wire.SubmittedInputs.All(input=>input.Count==8&&input.ContainsKey(7)&&input[7]=="1"),
                "both balance attempts bind all eight native inputs including the explicit SI7 quote profile before accepting a verified callback");
            wire.Complete();
            var source=Get<BrokerHoldingsSnapshot>(form,"tradeHoldings");
            Check(received==1&&!api.Busy&&source!=null&&source.RequestBasisVerified&&source.BasisVerified&&source.Holdings.Count==0,
                "only the accepted retry callback updates the actual form with verified synthetic balance data");
            Schedule(form,before.AddSeconds(3));
            Check(wire.Requests==2&&received==1,"later scheduler ticks do not repeat a completed read-only retry");
            Check(Get<LiveOrderGateway>(form,"gateway")==null&&Get<LiveOrderJournal>(form,"journal")==null&&Get<RealtimeIndi>(form,"realtime")==null
                &&Get<object>(form,"entry")==null&&Get<object>(form,"credentials")==null,"deferred read-only retry initializes no order channel account session or order intent");
            Close(form,data);
        }
    }

    static void Run(string data,string screenshot) {
        using(var form=NewForm()) {
            Check(form.Text.Contains("2.2.7"),"premarket regression runs against version 2.2.7");
            Check(All(form).OfType<Button>().Any(button=>button.Text=="조회 정의 내보내기"),"static query-definition export is available without invoking registry COM or provider queries");
            Check(!Get<LiveTradingSession>(form,"authorization").Active,"application startup never authorizes live orders");
            var fixture=new Fixture(form);Bind(form);
            Call(form,"BeginResearch",Opening.AddHours(-10),false);
            Check(!Get<bool>(form,"scanning")&&Prepared(form).Count==0&&fixture.Wire.Queries.Count==1,
                "before target-day midnight the reservation sends no unfinished daily-bar research requests");
            Begin(form);
            Check(Get<List<Stock>>(form,"symbols").Count==3,"synthetic stock master traverses the actual coordinator and form callback");
            Check(Get<DateTime?>(form,"researchDateKorea")==MarketTime.ToKorea(Opening).Date,"preparation is bound to the authorized trading date");
            FinishPreparation(form,fixture,0);
            Check(Prepared(form).Count==3&&Get<bool>(form,"researchComplete"),"preopen daily-bar callbacks complete and retain all prepared references");
            Check(Prepared(form).Values.All(x=>x.LastCompletedBarDate=="20261113"),"prepared references preserve the prior completed trading date");
            Check(Get<DataGridView>(form,"candidatesGrid").Rows.Count==3,"prepared watchlist rows are visible before opening");
            Check(!Get<UnattendedSession>(form,"session").SessionDateKorea.HasValue,"preopen preparation does not open the market trading session");
            Check(Get<Queue<Candidate>>(form,"buyQuotes").Count==0,"historical preopen references are not promoted into actionable buy candidates");
            CheckNoOrderTransport(form,"preopen preparation activates no realtime or order transport");
            var before=Prepared(form).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            int requests=fixture.Wire.Queries.Count;
            Schedule(form,Opening);
            Check(Get<bool>(form,"openingAdopted"),"opening adopts completed premarket preparation");
            Check(Prepared(form).Count==before.Count&&before.All(x=>ReferenceEquals(Prepared(form)[x.Key],x.Value)),"opening retains the original prepared reference objects");
            Check(Get<List<PremarketReference>>(form,"watchedReferences").Count==3,"opening monitors the prepared watchlist");
            Check(Get<Queue<Candidate>>(form,"buyQuotes").Count==0,"opening adoption waits for live validation before creating actionable buy candidates");
            Check(fixture.Wire.Queries.Count==requests,"opening does not restart stock master or full daily-bar discovery");
            Schedule(form,Opening.AddSeconds(1));
            CheckNoOrderTransport(form,"prepared history alone cannot queue an order without confirmed live SC");
            Check(Get<DataGridView>(form,"candidatesGrid").Rows.Count==3,"live quote wait preserves prepared candidate rows");
            var reference=before.Values.First();
            var live=new RealtimeQuote{Code=reference.Code,MarketCode="1",Clock="090001",Price=reference.PriorHigh+10,
                CumulativeVolume=reference.AverageVolume*2,ReceivedUtc=Opening.AddSeconds(1),SequenceConfirmed=false};
            var strategy=Get<AccountAppSettings>(form,"configuration").Strategy;
            Check(PremarketResearch.EvaluateLive(reference,live,strategy,Opening.AddSeconds(1))==null,"UI-prepared reference cannot become a signal from one unconfirmed SC update");
            live.SequenceConfirmed=true;
            var signal=PremarketResearch.EvaluateLive(reference,live,strategy,Opening.AddSeconds(1));
            Check(signal!=null&&signal.SignalDate=="20261116"&&reference.LastCompletedBarDate=="20261113","confirmed live validation produces a current-session signal without rewriting historical source date");

            var source=new BrokerHoldingsSnapshot{BasisVerified=true,RequestStartedUtc=DateTime.UtcNow.AddMilliseconds(-10),ReceivedUtc=DateTime.UtcNow};
            source.Holdings.Add(new Holding{Code="069500",Name="SYNTHETIC ETF",Quantity=2,AvgPrice=70000,Price=70000});
            source.BasisValidated["069500"]=true;source.CashSourceValidated["069500"]=true;source.BasisDiagnostics["069500"]="TradeBasisCode1";
            var requestType=typeof(MainForm).GetNestedType("EntryRequest",BindingFlags.NonPublic);
            var pending=Activator.CreateInstance(requestType,true);
            requestType.GetField("AutoSize").SetValue(pending,true);
            requestType.GetField("Intent").SetValue(pending,new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code=reference.Code,Quantity=1,LimitPrice=live.Price,ClientKey="entry-20261116-"+reference.Code});
            requestType.GetField("Candidate").SetValue(pending,signal);
            Set(form,"entry",pending);
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});
            Check(Get<List<PremarketReference>>(form,"watchedReferences").Any(x=>ReferenceEquals(x,reference)),"preflight holdings refresh retains the pending buy's original prepared reference");
            Check((bool)Call(form,"ValidEntrySignal",signal,live,Opening.AddSeconds(1)),"a pending buy remains signal-valid after fresh other-symbol holdings are observed");
            Set(form,"entry",null);
            Check(Get<List<Holding>>(form,"holdings").Single().Code=="069500"&&!Prepared(form).ContainsKey("069500"),"existing holdings remain monitored outside the prepared discovery universe");
            var decision=(string)Call(form,"EvaluateHoldingSell",source.Holdings[0],new IndiReply{Code="069500",Price=70000,Clock="090001",HasTradeClock=true},Opening.AddSeconds(1));
            Check(string.IsNullOrEmpty(decision),"opening preparation does not force a sale of existing holdings");
            string streamStatus=Get<Label>(form,"quoteStatus").Text;
            Call(form,"Reply",new IndiReply{Kind="QUOTE",Code="069500",Price=71000,HasTradeClock=false,Clock=""});
            Check(Get<Label>(form,"state").Text.Contains("조회 전용")&&Get<Label>(form,"state").Text.Contains("실시간 SC 상태는 별도")
                &&Get<Label>(form,"quoteStatus").Text==streamStatus,"manual snapshot's missing clock is described separately from realtime SC status");
            CandidateStatus(form,reference);
            Call(form,"UpdateResearchStatus",Opening.AddSeconds(1));
            if(screenshot!=null)Capture(form,screenshot);

            Get<Queue<Candidate>>(form,"buyQuotes").Enqueue(signal);
            var newlyHeld=new Holding{Code=reference.Code,Name="SYNTHETIC NEW HOLDING",Quantity=1,AvgPrice=live.Price,Price=live.Price};
            source.Holdings.Add(newlyHeld);source.BasisValidated[reference.Code]=true;source.CashSourceValidated[reference.Code]=true;source.BasisDiagnostics[reference.Code]="TradeBasisCode1";
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});
            Check(!Get<List<PremarketReference>>(form,"watchedReferences").Any(x=>x.Code==reference.Code)
                &&!Get<Queue<Candidate>>(form,"buyQuotes").Any(x=>x.Code==reference.Code),"a newly held symbol is removed from both watchlist and queued buy signals");
            source.Holdings.Remove(newlyHeld);
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});

            object saved=Call(form,"CaptureResearch");form.Halt();
            Check((bool)Call(form,"RestoreResearch",saved,BeforeOpen)&&Prepared(form).Count==3
                &&before.All(x=>ReferenceEquals(Prepared(form)[x.Key],x.Value)),"public research can survive account attachment's capture-stop-restore lifecycle");
            Check(!Get<LiveTradingSession>(form,"authorization").Active&&Get<object>(form,"credentials")==null
                &&Get<Queue<Candidate>>(form,"buyQuotes").Count==0&&Get<RealtimeIndi>(form,"realtime")==null,"restoring public research restores no account approval, credentials, realtime stream or buy signals");
            form.Halt();
            Check(!(bool)Call(form,"RestoreResearch",saved,BeforeOpen.AddDays(1))&&Prepared(form).Count==0,"public research from a different target trading day is refused");
            Call(form,"RestoreResearch",saved,BeforeOpen);

            Get<Dictionary<string,NumericUpDown>>(form,"options")["FastPeriod"].Value=6;
            Call(form,"SaveSettings");
            Check(Prepared(form).Count==0&&Get<List<PremarketReference>>(form,"watchedReferences").Count==0,"strategy save invalidates all prepared and watched references");
            Check(!Get<LiveTradingSession>(form,"authorization").Active,"strategy save requires explicit new live authorization");
            Check(Get<List<Holding>>(form,"holdings").Count==1,"strategy change preserves last received holdings for display");
            Check(!(bool)Call(form,"RestoreResearch",saved,BeforeOpen)&&Prepared(form).Count==0,"public research from a different strategy is refused");
            Close(form,data);
        }
        using(var form=NewForm()) {
            var fixture=new Fixture(form);Bind(form);Begin(form);
            Schedule(form,BeforeOpen.AddSeconds(1));fixture.Wire.Complete();
            Check(Prepared(form).Count==1,"partial preparation retains the first completed reference");
            var first=Prepared(form).Values.Single();
            Schedule(form,BeforeOpen.AddSeconds(2));
            Check(fixture.Api.Busy,"a later preopen daily-bar request can remain in flight");
            int beforeOpenRequests=fixture.Wire.Queries.Count;
            Schedule(form,Opening);
            Check(Prepared(form).Count==1&&ReferenceEquals(Prepared(form)[first.Code],first),"opening while a bar request is in flight preserves completed references");
            Check(fixture.Api.Busy&&fixture.Wire.Queries.Count==beforeOpenRequests,"opening does not cancel or duplicate the in-flight research request");
            fixture.Wire.Complete();Schedule(form,Opening.AddSeconds(1));
            Check(Get<bool>(form,"openingAdopted")&&ReferenceEquals(Prepared(form)[first.Code],first),"partial research is adopted after the pending response without clearing earlier work");
            // The remaining discovery may proceed after adoption. Only complete
            // requests issued by the scheduler; no forced extra scan is started.
            for(int i=0;i<6&&!Get<bool>(form,"researchComplete");i++) {
                if(fixture.Api.Busy)fixture.Wire.Complete();
                Schedule(form,Opening.AddSeconds(i+2));
            }
            Check(Prepared(form).Count==3&&Get<bool>(form,"researchComplete"),"remaining preopen research finishes during the session without restarting the scan");
            Check(fixture.Wire.Queries.Count(x=>x=="stock_mst")==1&&fixture.Wire.Queries.Count(x=>x=="TR_SCHART")==3,"partial transition queries each discovery symbol exactly once");
            CheckNoOrderTransport(form,"partial opening transition never creates a financial order from historical bars");
            Close(form,data);
        }
        RealtimeStatus(data);
        OrdinarySourceStatus(data);
        RejectedOrderStatus(data);
        AcceptedUnknownStatus(data);
        RejectedSellQueue(data);
        AcceptedRealtimeFaultCases.Run(data,Check);
        DeferredReadStatus(data);
    }

    [STAThread] static int Main(string[] arguments) {
        string result=arguments.Length>0?Path.GetFullPath(arguments[0]):null;
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShinhanNativeTrader");
        bool ownsData=false;int exit=0;
        try {
            bool windows=Environment.OSVersion.Platform==PlatformID.Win32NT;
            bool linux=arguments.Contains("--isolated-linux-smoke")&&data.StartsWith("/tmp/shinhan-premarket-ui-",StringComparison.Ordinal);
            if(!(windows&&Environment.GetEnvironmentVariable("GITHUB_ACTIONS")=="true")&&!linux)
                throw new InvalidOperationException("Premarket UI tests require a fresh GitHub Actions Windows profile or explicitly isolated Linux directory.");
            if(Directory.Exists(data)||File.Exists(data))throw new InvalidOperationException("Refusing to touch existing application data.");
            if(result==null||result.StartsWith(data+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An external result path is required.");
            ownsData=true;CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            int image=Array.IndexOf(arguments,"--screenshot");
            Run(data,image>=0?Path.GetFullPath(arguments[image+1]):null);
            Results.AppendLine("PASS "+assertions+" premarket UI assertions; synthetic data only, no actual Indi server or money orders.");
        }catch(Exception error){exit=1;Results.AppendLine("FAIL "+error);}
        finally {
            if(ownsData&&Directory.Exists(data))try{Directory.Delete(data,true);}catch(Exception error){exit=1;Results.AppendLine("FAIL test profile cleanup: "+error);}
            Console.Write(Results.ToString());
            if(result!=null)try{Directory.CreateDirectory(Path.GetDirectoryName(result));File.WriteAllText(result,Results.ToString(),Encoding.UTF8);}
                catch(Exception error){Console.Error.WriteLine(error);exit=1;}
        }
        return exit;
    }
}

// Synthetic integration only: the actual form's ACK/AA/AD subscribers run,
// while the broker boundary is an in-memory wire and no COM object is created.
static class AcceptedRealtimeFaultCases {
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly DateTime Utc=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
    static T Get<T>(object owner,string name){return (T)owner.GetType().GetField(name,Hidden).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Hidden).SetValue(owner,value);}
    static void Call(object owner,string name,params object[] values){
        try{owner.GetType().GetMethod(name,Hidden).Invoke(owner,values);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    static T Handler<T>(MainForm form,string name) where T:class {
        return Delegate.CreateDelegate(typeof(T),form,typeof(MainForm).GetMethod(name,Hidden)) as T;
    }
    sealed class Wire : ILiveOrderWire {
        public event Action<LiveOrderAcknowledgement> Acknowledged;
        public int Sends;
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc){Sends++;return 2201;}
        public void Accept(){
            var values=new[]{"0","000812345","0","777","0","synthetic accepted","0","0"};
            var evidence=LiveOrderResponseParser.Read(2201,index=>values[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{"12345678901","1111"});
            if(Acknowledged!=null)Acknowledged(new LiveOrderAcknowledgement{RequestId=2201,BrokerOrderNumber=evidence.BrokerOrderNumber,ResponseEvidence=evidence});
        }
        public void Dispose(){}
    }
    static Dictionary<int,object> AA(string clock) {
        return new Dictionary<int,object>{{0,"03"},{2,"12345678901"},{4,"01"},{5,"812345"},{6,"0"},{8,"01"},{9,"01"},{10,"02"},{11,"A069500"},
            {14,"2"},{15,"86295"},{16,"2"},{19,"0"},{20,"0"},{21,"091531"},{22,"F0002"},{23,"2"},{24,"86295"},{25,clock},{32,"0"}};
    }
    static Dictionary<int,object> AD() {
        return new Dictionary<int,object>{{0,"12345678901"},{1,"01"},{2,"A069500"},{3,"0"},{6,"0"},{7,""},{8,"0"},{9,"0"},{20,"86295"}};
    }
    public static void Run(string data,Action<bool,string> check) {
        foreach(var scenario in new[]{"AA-valid","AA-clock8","AD-blank-average"})RunCase(data,scenario,check);
    }
    static void RunCase(string data,string scenario,Action<bool,string> check) {
        using(var form=new MainForm()) {
            Get<Timer>(form,"scheduler").Stop();form.ShowInTaskbar=false;form.Show();Application.DoEvents();Get<Timer>(form,"scheduler").Stop();
            var credentialType=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credential=Activator.CreateInstance(credentialType,new object[]{"12345678901","1111"});
            var key=(string)credentialType.GetProperty("Key").GetValue(credential,null);
            Set(form,"credentials",credential);Set(form,"boundKey",key);Set(form,"lastReportKey",key);
            var authorization=Get<LiveTradingSession>(form,"authorization");authorization.Authorize("12345678901",Utc);
            var session=Get<UnattendedSession>(form,"session");session.Arm(Utc);session.Tick(Utc,false);
            string path=Path.Combine(data,"accepted-rt-"+scenario+".xml");
            using(var journal=new LiveOrderJournal(path)) {
                journal.BindAccount("12345678901");Set(form,"journal",journal);
                var readiness=new BrokerReadiness(key,journal.Snapshot.AccountFingerprint);readiness.SetAuthorization(true);readiness.SetAccountStream(true,true,Utc);Set(form,"readiness",readiness);
                var router=new BrokerAccountEventRouter(journal,Handler<Action<DateTime,string>>(form,"OnAccountInvalidated"));
                router.Changed+=Handler<Action<LiveOrderRecord>>(form,"OnBrokerOrderChanged");
                router.SetBrokerDate(MarketTime.ToKorea(Utc).ToString("yyyyMMdd",CultureInfo.InvariantCulture),Utc);Set(form,"accountEvents",router);
                var fields=AA("091532");var processor=new RealtimeEventProcessor(index=>fields.ContainsKey(index)?fields[index]:"");processor.BindAccount("12345678901");
                var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
                Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>());Set(realtime,"native",new object());
                Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(form,"realtime",realtime);
                processor.OrderReceived+=Handler<Action<CashOrderEvent>>(form,"OnRealtimeOrder");
                processor.BalanceReceived+=Handler<Action<CashBalanceEvent>>(form,"OnRealtimeBalance");
                processor.Faulted+=ignored=>Call(form,"OnRealtimeFailure","UNTRUSTED account12345678901 raw09153212");
                var wire=new Wire();var gateway=new LiveOrderGateway(journal,wire,()=>Utc);gateway.OrderChanged+=Handler<Action<LiveOrderRecord>>(form,"OnGatewayOrderChanged");Set(form,"gateway",gateway);
                var checks=new LiveOrderPreflight{Authorization=authorization,EvidenceStillCurrent=()=>true,AccountAuthorized=true,OrderableAmountVerified=true,
                    SellableQuantityVerified=true,OrdersReconciled=true,FillsReconciled=true,RecoveryVerified=true,PriceRuleVerified=true,QuoteVerified=true,
                    SellableQuantity=2,SnapshotUtc=Utc,QuoteUtc=Utc,OperatingLimit=1000000};
                var sent=gateway.Submit(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="069500",Quantity=2,LimitPrice=86295,ClientKey="synthetic-"+scenario},
                    new LiveAccountCredentials("12345678901","1111"),checks,Utc);
                check(wire.Sends==1&&sent.Status==LiveOrderStatus.AwaitingAcknowledgement,scenario+": fake wire submission waits for actual ACK subscriber processing");
                wire.Accept();var accepted=journal.Snapshot.Orders.Single();
                check(accepted.Status==LiveOrderStatus.Working&&accepted.BrokerOrderNumber=="000812345"&&accepted.ResponseEvidence.Disposition==OrderResponseDisposition.Accepted,
                    scenario+": modern positive KRX ACK traverses actual form listeners and remains accepted before RT data");
                if(scenario=="AA-clock8")fields=AA("09153212");
                if(scenario=="AD-blank-average")fields=AD();
                processor.Process(scenario.StartsWith("AD",StringComparison.Ordinal)?"AD":"AA",Utc.AddSeconds(2));
                if(scenario=="AA-valid") {
                    var filled=journal.Snapshot.Orders.Single();
                    check(!processor.Failed&&filled.Status==LiveOrderStatus.Filled&&filled.FilledQuantity==2&&journal.Snapshot.Executions.Count==1,
                        scenario+": valid six-digit AA execution traverses the actual router and preserves a confirmed fill");
                }else {
                    var failure=processor.LastFailure;
                    check(processor.Failed&&failure!=null&&failure.FieldIndex==(scenario=="AA-clock8"?25:7),
                        scenario+": RT parser preserves the exact synthetic failure field without publishing a fabricated fill");
                    LiveOrderJournalState persisted;
                    using(var saved=File.OpenRead(path))persisted=(LiveOrderJournalState)new System.Xml.Serialization.XmlSerializer(typeof(LiveOrderJournalState)).Deserialize(saved);
                    {
                        var uncertain=persisted.Orders.Single();
                        check(uncertain.Status==LiveOrderStatus.Unknown&&uncertain.BrokerOrderNumber=="000812345"
                            &&uncertain.ResponseEvidence.Disposition==OrderResponseDisposition.Accepted&&uncertain.FilledQuantity==0&&persisted.Executions.Count==0,
                            scenario+": actual RT-failure handler stops operations while retaining accepted broker identity and ACK evidence durably");
                    }
                    check(Get<object>(form,"credentials")==null&&!authorization.Active&&wire.Sends==1,
                        scenario+": RT failure revokes the account session and never resends the accepted order");
                    var grid=Get<DataGridView>(form,"ordersGrid");
                    check(grid.Rows.Count==1&&Convert.ToString(grid.Rows[0].Cells["주문번호"].Value,CultureInfo.InvariantCulture)=="000812345",
                        scenario+": the actual order grid retains the positive broker order number after reconciliation becomes necessary");
                    check(Convert.ToString(grid.Rows[0].Cells["확인상태"].Value,CultureInfo.InvariantCulture)=="접수 확인 · 체결 대사 필요"
                        &&Get<TextBox>(form,"orderResponseDetails").Text.Contains("result=Accepted"),
                        scenario+": stopped order display preserves broker acceptance while explicitly requiring fill reconciliation");
                    string diagnostic=Get<Label>(form,"realtimeFailureStatus").Text;
                    string logged=Get<TextBox>(form,"logs").Text;
                    check(diagnostic.Contains(failure.SafeDiagnostic)&&diagnostic.Contains("kind="+failure.Kind)
                        &&diagnostic.Contains("field="+failure.FieldIndex.ToString(CultureInfo.InvariantCulture))&&diagnostic.Contains("shape=")
                        &&logged.Contains(failure.SafeDiagnostic)&&!logged.Contains("UNTRUSTED")&&!logged.Contains("raw09153212")&&!logged.Contains("12345678901"),
                        scenario+": parser-owned kind field and shape survive Halt visibly and in logs without arbitrary callback text");
                }
                form.Close();
                using(var lease=new FileStream(Path.Combine(data,"live-settings-v1.xml.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    check(lease.CanWrite,scenario+": actual form cleanup releases its settings lease");
            }
        }
    }
}
