using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace ShinhanNativeTrader {
    public enum PaperOrderSide { Buy, Sell }
    public enum PaperOrderStatus { Working, PartiallyFilled, Filled, Cancelled, Rejected, NeedsReconciliation }
    public sealed class PaperOrder {
        public string Id, ClientKey, Code, Name, RejectionReason;
        public PaperOrderSide Side;
        public PaperOrderStatus Status;
        public int Quantity, FilledQuantity;
        public decimal LimitPrice, FeeRate, ReservedCash;
        public DateTime CreatedUtc;
    }
    public sealed class PaperFill {
        public string Id, OrderId;
        public int Quantity;
        public decimal Price;
        public DateTime FilledUtc;
    }
    public sealed class PaperTradingState {
        public int Version = 1;
        public string Mode = "Paper";
        public decimal Capital, Cash;
        public DateTime UpdatedUtc;
        public StrategySettings Settings = new StrategySettings();
        public List<PaperOrder> Orders = new List<PaperOrder>();
        public List<PaperFill> Fills = new List<PaperFill>();
        public List<PaperPosition> Positions = new List<PaperPosition>();
    }
    public static class LiveOrderPolicy {
        public static bool Enabled { get { return false; } }
        public const string BlockReason = "실주문 차단: 공식 주문/체결/미체결/주문가능금액 인터페이스, Windows Indi 실서버 재연결·복구 검증 및 별도 사용자 승인이 완료되지 않았습니다.";
        public static void EnsureAllowed() { throw new InvalidOperationException(BlockReason); }
    }
    // This ledger is deliberately paper-only. Its cash/fills never represent broker money or execution.
    // Every mutation is validated and durably saved before becoming visible to the caller.
    public sealed class PaperTradingEngine : IDisposable {
        static readonly XmlSerializer Serializer = new XmlSerializer(typeof(PaperTradingState));
        readonly string _path;
        readonly FileStream _lease;
        readonly object _sync = new object();
        PaperTradingState _state;
        bool _disposed, _storageFault;
        public PaperTradingEngine(string path, decimal initialCapital, StrategySettings settings = null) {
            if(initialCapital <= 0) throw new ArgumentOutOfRangeException("initialCapital");
            _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            try {
                if(_path != null) {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                if(_path != null && File.Exists(_path)) {
                    using(var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        _state = (PaperTradingState)Serializer.Deserialize(file);
                    Validate(_state);
                    // A crashed submit/fill sequence is ambiguous. Preserve remaining cash/stock
                    // reservations and require an explicit local paper recovery action.
                    if(_state.Orders.Any(IsPending)) Change(s => {
                        foreach(var order in s.Orders.Where(IsPending)) order.Status = PaperOrderStatus.NeedsReconciliation;
                    });
                } else {
                    var chosen = (settings ?? new StrategySettings()).Copy(); chosen.Validate();
                    _state = new PaperTradingState { Capital = initialCapital, Cash = initialCapital, Settings = chosen, UpdatedUtc = DateTime.UtcNow };
                    Validate(_state); Save(_state);
                }
            } catch {
                if(_lease != null) _lease.Dispose();
                throw;
            }
        }
        public PaperTradingState Snapshot { get { lock(_sync) { CheckUsable(); return Copy(_state); } } }
        public StrategySettings Settings { get { lock(_sync) { CheckUsable(); return _state.Settings.Copy(); } } }
        public decimal AvailableCash { get { lock(_sync) { CheckUsable(); return Available(_state); } } }
        public bool RecoveryRequired { get { lock(_sync) { CheckUsable(); return _state.Orders.Any(o => o.Status == PaperOrderStatus.NeedsReconciliation); } } }
        public bool StorageFault { get { return _storageFault; } }
        public void UpdateSettings(StrategySettings settings) {
            if(settings == null) throw new ArgumentNullException("settings");
            var chosen = settings.Copy(); chosen.Validate();
            Change(s => s.Settings = chosen);
        }
        public void SetCapital(decimal totalCapital) {
            if(totalCapital <= 0) throw new ArgumentOutOfRangeException("totalCapital");
            Change(s => {
                var cash = s.Cash + totalCapital - s.Capital;
                if(cash < s.Orders.Sum(o => o.ReservedCash)) throw new InvalidOperationException("투입 중인 금액과 미체결 예약금보다 원금을 낮출 수 없습니다.");
                s.Cash = cash; s.Capital = totalCapital;
            });
        }
        public PaperOrder SubmitBuy(string code, string name, int qty, decimal limitPrice, string clientKey, DateTime utc) {
            return Submit(PaperOrderSide.Buy,code,name,qty,limitPrice,clientKey,utc);
        }
        public PaperOrder SubmitSell(string code, int qty, decimal limitPrice, string clientKey, DateTime utc) {
            return Submit(PaperOrderSide.Sell,code,code,qty,limitPrice,clientKey,utc);
        }
        PaperOrder Submit(PaperOrderSide side, string code, string name, int qty, decimal price, string key, DateTime utc) {
            if(Rules.Code(code) != code || qty <= 0 || qty > 100000 || price <= 0 || string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("유효한 종목, 수량, 가격, 중복방지 키가 필요합니다.");
            RequireUtc(utc);
            PaperOrder result = null;
            Change(s => {
                if(s.Orders.Any(o => o.Status == PaperOrderStatus.NeedsReconciliation)) throw new InvalidOperationException("재시작 미확정 모의주문을 먼저 복구하십시오.");
                if(s.Orders.Any(o => o.ClientKey == key)) throw new InvalidOperationException("중복 주문 키가 이미 기록돼 있습니다.");
                if(s.Orders.Any(o => o.Code == code && IsPending(o))) throw new InvalidOperationException("동일 종목의 미체결 주문이 있습니다.");
                var position = s.Positions.SingleOrDefault(p => p.Code == code);
                if(side == PaperOrderSide.Buy) {
                    if(position != null) throw new InvalidOperationException("이미 보유한 종목은 추가 매수하지 않습니다.");
                    var slots = s.Positions.Select(p => p.Code).Union(s.Orders.Where(o => o.Side == PaperOrderSide.Buy && IsPending(o)).Select(o => o.Code)).Count();
                    if(slots >= s.Settings.MaxPositions) throw new InvalidOperationException("최대 보유 종목 수를 초과합니다.");
                    if(price < s.Settings.MinPrice || price > s.Settings.MaxPrice) throw new InvalidOperationException("전략의 가격 범위를 벗어났습니다.");
                    if(price * qty * (1 + s.Settings.FeeRate) > Available(s)) throw new InvalidOperationException("모의 주문가능금액 부족");
                } else if(position == null || qty > position.Qty) throw new InvalidOperationException("모의 매도가능수량 부족");
                result = new PaperOrder { Id = Guid.NewGuid().ToString("N"), ClientKey = key, Code = code, Name = name ?? code,
                    Side = side, Status = PaperOrderStatus.Working, Quantity = qty, LimitPrice = price,
                    FeeRate = s.Settings.FeeRate, CreatedUtc = utc,
                    ReservedCash = side == PaperOrderSide.Buy ? price * qty * (1 + s.Settings.FeeRate) : 0 };
                s.Orders.Add(result);
            });
            return CopyOrder(result);
        }
        public bool ApplyFill(string orderId, string fillId, int qty, decimal price, DateTime utc) {
            if(qty <= 0 || price <= 0 || string.IsNullOrWhiteSpace(fillId)) throw new ArgumentException("체결번호, 양수 수량과 가격이 필요합니다.");
            RequireUtc(utc);
            bool applied = false;
            Change(s => {
                var duplicate = s.Fills.SingleOrDefault(f => f.Id == fillId);
                if(duplicate != null) {
                    if(duplicate.OrderId != orderId || duplicate.Quantity != qty || duplicate.Price != price)
                        throw new InvalidOperationException("동일 체결번호의 내용이 다릅니다.");
                    return;
                }
                var order = FindOrder(s,orderId);
                if(order.Status != PaperOrderStatus.Working && order.Status != PaperOrderStatus.PartiallyFilled)
                    throw new InvalidOperationException("신규 체결을 수용할 수 없는 주문 상태입니다.");
                if(utc < order.CreatedUtc) throw new InvalidOperationException("주문 이전 시각의 체결입니다.");
                if(qty > order.Quantity - order.FilledQuantity || (order.Side == PaperOrderSide.Buy && price > order.LimitPrice) ||
                   (order.Side == PaperOrderSide.Sell && price < order.LimitPrice)) throw new InvalidOperationException("체결수량/지정가 검증 실패");
                var position = s.Positions.SingleOrDefault(p => p.Code == order.Code);
                if(order.Side == PaperOrderSide.Buy) {
                    var cost = price * qty * (1 + order.FeeRate);
                    if(cost > s.Cash) throw new InvalidOperationException("체결 반영에 필요한 현금 부족");
                    s.Cash -= cost;
                    if(position == null) {
                        position = new PaperPosition { Code = order.Code, Name = order.Name, Qty = qty, Entry = price, High = price };
                        s.Positions.Add(position);
                    } else {
                        position.Entry = (position.Entry * position.Qty + price * qty) / (position.Qty + qty);
                        position.Qty += qty; position.High = Math.Max(position.High,price);
                    }
                } else {
                    if(position == null || qty > position.Qty) throw new InvalidOperationException("체결 반영에 필요한 보유수량 부족");
                    position.Qty -= qty; s.Cash += price * qty * (1 - order.FeeRate);
                    if(position.Qty == 0) s.Positions.Remove(position);
                }
                order.FilledQuantity += qty;
                order.Status = order.FilledQuantity == order.Quantity ? PaperOrderStatus.Filled : PaperOrderStatus.PartiallyFilled;
                order.ReservedCash = order.Side == PaperOrderSide.Buy ? (order.Quantity - order.FilledQuantity) * order.LimitPrice * (1 + order.FeeRate) : 0;
                s.Fills.Add(new PaperFill { Id = fillId, OrderId = order.Id, Quantity = qty, Price = price, FilledUtc = utc });
                applied = true;
            });
            return applied;
        }
        public void CancelOrder(string orderId) { CloseOrder(orderId,PaperOrderStatus.Cancelled,""); }
        public void RejectOrder(string orderId, string reason) { CloseOrder(orderId,PaperOrderStatus.Rejected,reason ?? ""); }
        void CloseOrder(string id, PaperOrderStatus status, string reason) {
            Change(s => {
                var order = FindOrder(s,id);
                if(order.Status == status) return;
                if(!IsPending(order)) throw new InvalidOperationException("이미 종료된 주문입니다.");
                order.Status = status; order.ReservedCash = 0; order.RejectionReason = reason;
            });
        }
        public int RecoverPaperOrders() {
            int count = 0;
            Change(s => {
                foreach(var order in s.Orders.Where(o => o.Status == PaperOrderStatus.NeedsReconciliation)) {
                    // This is local simulated cancellation, never a claim that broker orders were cancelled.
                    order.Status = PaperOrderStatus.Cancelled; order.ReservedCash = 0; order.RejectionReason = "재시작 후 모의 미체결 취소"; count++;
                }
            });
            return count;
        }
        public string UpdateQuote(string code, decimal price) {
            if(Rules.Code(code) != code || price <= 0) throw new ArgumentException("유효한 종목과 가격이 필요합니다.");
            string exit = "";
            Change(s => {
                var position = s.Positions.SingleOrDefault(p => p.Code == code);
                if(position == null) return;
                position.High = Math.Max(position.High,price);
                exit = Rules.Exit(position,price,s.Settings);
            });
            return exit;
        }
        public void ExpireWorkingOrders(DateTime utc, TimeSpan lifetime) {
            RequireUtc(utc);
            if(lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("lifetime");
            Change(s => {
                foreach(var order in s.Orders.Where(o => (o.Status == PaperOrderStatus.Working || o.Status == PaperOrderStatus.PartiallyFilled) && utc - o.CreatedUtc >= lifetime)) {
                    order.Status = PaperOrderStatus.Cancelled; order.ReservedCash = 0; order.RejectionReason = "모의 미체결 만료";
                }
            });
        }
        static PaperOrder FindOrder(PaperTradingState state, string id) {
            var order = state.Orders.SingleOrDefault(o => o.Id == id);
            if(order == null) throw new InvalidOperationException("주문번호를 찾을 수 없습니다.");
            return order;
        }
        static bool IsPending(PaperOrder o) { return o.Status == PaperOrderStatus.Working || o.Status == PaperOrderStatus.PartiallyFilled || o.Status == PaperOrderStatus.NeedsReconciliation; }
        static decimal Available(PaperTradingState s) { return s.Cash - s.Orders.Sum(o => o.ReservedCash); }
        static void RequireUtc(DateTime utc) { if(utc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC timestamp required"); }
        void CheckUsable() {
            if(_disposed) throw new ObjectDisposedException("PaperTradingEngine");
            if(_storageFault) throw new InvalidOperationException("모의 상태 저장 실패로 거래가 차단됐습니다. 파일을 확인하고 재시작하십시오.");
        }
        void Change(Action<PaperTradingState> mutation) {
            lock(_sync) {
                CheckUsable();
                var next = Copy(_state);
                mutation(next); next.UpdatedUtc = DateTime.UtcNow; Validate(next);
                try { Save(next); } catch { _storageFault = true; throw; }
                _state = next;
            }
        }
        void Save(PaperTradingState state) {
            if(_path == null) return;
            var temp = _path + ".tmp." + Guid.NewGuid().ToString("N");
            try {
                using(var file = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    Serializer.Serialize(file,state); file.Flush(true);
                }
                if(File.Exists(_path)) File.Replace(temp,_path,null);
                else File.Move(temp,_path);
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        static PaperTradingState Copy(PaperTradingState value) {
            using(var stream = new MemoryStream()) { Serializer.Serialize(stream,value); stream.Position = 0; return (PaperTradingState)Serializer.Deserialize(stream); }
        }
        static PaperOrder CopyOrder(PaperOrder o) {
            return new PaperOrder { Id = o.Id, ClientKey = o.ClientKey, Code = o.Code, Name = o.Name, Side = o.Side,
                Status = o.Status, Quantity = o.Quantity, FilledQuantity = o.FilledQuantity, LimitPrice = o.LimitPrice,
                FeeRate = o.FeeRate, ReservedCash = o.ReservedCash, CreatedUtc = o.CreatedUtc, RejectionReason = o.RejectionReason };
        }
        static void Validate(PaperTradingState s) {
            if(s == null || s.Version != 1 || s.Mode != "Paper" || s.Settings == null || s.Capital <= 0 || s.Cash < 0 ||
               s.Orders == null || s.Fills == null || s.Positions == null) throw new InvalidDataException("지원하지 않거나 손상된 모의 상태입니다.");
            s.Settings.Validate();
            if(s.Orders.Any(o => o == null || string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.ClientKey) ||
                Rules.Code(o.Code) != o.Code || o.Quantity <= 0 || o.Quantity > 100000 || o.FilledQuantity < 0 || o.FilledQuantity > o.Quantity || o.LimitPrice <= 0 ||
                o.FeeRate < 0 || o.FeeRate > 0.1m || o.CreatedUtc.Kind != DateTimeKind.Utc || !Enum.IsDefined(typeof(PaperOrderStatus),o.Status) || !Enum.IsDefined(typeof(PaperOrderSide),o.Side)))
                throw new InvalidDataException("손상된 모의주문입니다.");
            if(s.Orders.GroupBy(o => o.Id).Any(g => g.Count() > 1) || s.Orders.GroupBy(o => o.ClientKey).Any(g => g.Count() > 1) ||
               s.Fills.GroupBy(f => f.Id).Any(g => g.Count() > 1) || s.Positions.GroupBy(p => p.Code).Any(g => g.Count() > 1))
                throw new InvalidDataException("중복 모의 식별자입니다.");
            if(s.Fills.Any(f => f == null || string.IsNullOrWhiteSpace(f.Id) || f.Quantity <= 0 || f.Price <= 0 || !s.Orders.Any(o => o.Id == f.OrderId)))
                throw new InvalidDataException("손상된 모의체결입니다.");
            decimal expectedCash = s.Capital;
            var quantities = new Dictionary<string,int>();
            foreach(var o in s.Orders) {
                var fills = s.Fills.Where(f => f.OrderId == o.Id).ToArray();
                if(fills.Sum(f => f.Quantity) != o.FilledQuantity) throw new InvalidDataException("모의 체결합계가 주문과 다릅니다.");
                var reserve = o.Side == PaperOrderSide.Buy && IsPending(o) ? (o.Quantity-o.FilledQuantity)*o.LimitPrice*(1+o.FeeRate) : 0;
                if(o.ReservedCash != reserve || (o.Status == PaperOrderStatus.Working && o.FilledQuantity != 0) ||
                   (o.Status == PaperOrderStatus.PartiallyFilled && o.FilledQuantity == 0) || (o.Status == PaperOrderStatus.Filled && o.FilledQuantity != o.Quantity) ||
                   ((o.Status == PaperOrderStatus.Working || o.Status == PaperOrderStatus.PartiallyFilled || o.Status == PaperOrderStatus.NeedsReconciliation) && o.FilledQuantity == o.Quantity))
                    throw new InvalidDataException("모의 주문상태/예약금이 일치하지 않습니다.");
                foreach(var f in fills) {
                    if(f.FilledUtc.Kind != DateTimeKind.Utc || f.FilledUtc < o.CreatedUtc || (o.Side == PaperOrderSide.Buy && f.Price > o.LimitPrice) || (o.Side == PaperOrderSide.Sell && f.Price < o.LimitPrice))
                        throw new InvalidDataException("모의 체결 지정가가 일치하지 않습니다.");
                    expectedCash += f.Price*f.Quantity*(o.Side == PaperOrderSide.Buy ? -(1+o.FeeRate) : (1-o.FeeRate));
                    if(!quantities.ContainsKey(o.Code)) quantities[o.Code] = 0;
                    quantities[o.Code] += o.Side == PaperOrderSide.Buy ? f.Quantity : -f.Quantity;
                }
            }
            if(expectedCash != s.Cash || Available(s) < 0 || quantities.Any(p => p.Value < 0)) throw new InvalidDataException("모의 현금/예약금/수량 검증 실패");
            if(s.Positions.Any(p => p == null || Rules.Code(p.Code) != p.Code || p.Qty <= 0 || p.Entry <= 0 || p.High < p.Entry || !quantities.ContainsKey(p.Code) || quantities[p.Code] != p.Qty) ||
               quantities.Any(q => q.Value > 0 && !s.Positions.Any(p => p.Code == q.Key))) throw new InvalidDataException("모의 보유잔고가 체결과 다릅니다.");
            // Replay fill insertion order to verify cost basis as well as aggregate quantities.
            // High-water marks are persisted quote history and must stay at least the cost basis.
            var replay = new Dictionary<string,PaperPosition>();
            foreach(var f in s.Fills) {
                var order = FindOrder(s,f.OrderId);
                PaperPosition position; replay.TryGetValue(order.Code,out position);
                if(order.Side == PaperOrderSide.Buy) {
                    if(position == null) replay[order.Code] = new PaperPosition { Qty = f.Quantity, Entry = f.Price };
                    else {
                        position.Entry = (position.Entry*position.Qty + f.Price*f.Quantity)/(position.Qty+f.Quantity);
                        position.Qty += f.Quantity;
                    }
                } else {
                    if(position == null || f.Quantity > position.Qty) throw new InvalidDataException("모의 체결순서의 보유수량이 부족합니다.");
                    position.Qty -= f.Quantity;
                    if(position.Qty == 0) replay.Remove(order.Code);
                }
            }
            if(s.Positions.Any(p => !replay.ContainsKey(p.Code) || replay[p.Code].Entry != p.Entry))
                throw new InvalidDataException("모의 평균단가가 체결과 다릅니다.");
            if(s.Orders.Where(IsPending).GroupBy(o => o.Code).Any(g => g.Count() > 1)) throw new InvalidDataException("동일 종목 모의 미체결이 중복됐습니다.");
        }
        public void Dispose() {
            lock(_sync) { if(_disposed) return; _disposed = true; if(_lease != null) _lease.Dispose(); }
        }
    }
}
