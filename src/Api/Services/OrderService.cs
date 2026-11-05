using Api.Data;
using Api.Infra;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public record ItemInput(int ProductId, int Qty, int? PreferredStoreId, string? Source);
public record PreviewInput(List<ItemInput>? Items);
public record PaymentInput(string? Simulate);
public record CreateOrderInput(List<ItemInput>? Items, PaymentInput? Payment);

public class OrderService(AppDb db, IClock clock)
{
    public const int Shipping = 3500;

    record Line(Product Product, int Qty, int StoreId, string StoreName, int Unit, string? Reason, int Baseline, string Source);

    async Task<List<Line>> ResolveAsync(List<ItemInput>? items, DateOnly date)
    {
        if (items is null || items.Count == 0) throw ApiException.BadRequest("EMPTY_ORDER", "상품을 1개 이상 담아 주세요.");
        if (items.Count > 30) throw ApiException.BadRequest("TOO_MANY_ITEMS", "상품은 30개까지 담을 수 있습니다.");
        var stores = await db.Stores.AsNoTracking().ToListAsync();
        var open = stores.Where(s => StoreCalendar.IsOpen(s.ClosedRule, date)).ToDictionary(s => s.Id);
        var ids = items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var sps = await db.StoreProducts.AsNoTracking().Where(sp => ids.Contains(sp.ProductId)).ToListAsync();
        var lines = new List<Line>();
        foreach (var it in items)
        {
            if (it.Qty is < 1 or > 99) throw ApiException.BadRequest("INVALID_QTY", "수량은 1~99개입니다.");
            if (!products.TryGetValue(it.ProductId, out var p)) throw ApiException.BadRequest("UNKNOWN_PRODUCT", $"상품 {it.ProductId}을 찾을 수 없습니다.");
            var all = sps.Where(sp => sp.ProductId == p.Id).ToList();
            var candidates = all.Where(sp => open.ContainsKey(sp.StoreId) && sp.StockQty >= it.Qty).OrderBy(sp => sp.Price).ThenBy(sp => sp.StoreId).ToList();
            if (it.PreferredStoreId is int pref && all.All(sp => sp.StoreId != pref))
                throw ApiException.BadRequest("INVALID_STORE", "선택한 상점이 이 상품을 팔지 않습니다.");
            if (candidates.Count == 0) throw ApiException.Conflict("OUT_OF_STOCK", $"{p.Name}은(는) 지금 살 수 있는 상점이 없습니다.");
            var pick = candidates[0];
            string? reason = null;
            if (it.PreferredStoreId is int pr)
            {
                var chosen = candidates.FirstOrDefault(sp => sp.StoreId == pr);
                if (chosen is not null) pick = chosen;
                else reason = open.ContainsKey(pr) ? "선택한 상점의 재고가 부족해 최저가 상점으로 바꿨어요." : "선택한 상점이 쉬는 날이라 최저가 상점으로 바꿨어요.";
            }
            var baseline = (int)Math.Round(candidates.Average(c => c.Price));
            lines.Add(new Line(p, it.Qty, pick.StoreId, stores.First(s => s.Id == pick.StoreId).Name, pick.Price, reason, baseline, it.Source ?? "Manual"));
        }
        return lines;
    }

    public async Task<object> PreviewAsync(PreviewInput input)
    {
        var now = await clock.NowKstAsync();
        var (date, slot) = BatchSchedule.Next(now);
        var lines = await ResolveAsync(input.Items, date);
        var sum = lines.Sum(l => l.Unit * l.Qty);
        var baseline = lines.Sum(l => l.Baseline * l.Qty) + Shipping;
        var total = sum + Shipping;
        return new
        {
            batch = new { businessDate = date.ToString("yyyy-MM-dd"), slot = slot.ToString(), label = BatchSchedule.SlotLabel(slot), cutoffAt = BatchSchedule.CutoffKst(date, slot).ToString("yyyy-MM-ddTHH:mm:ss") },
            items = lines.Select(l => new { productId = l.Product.Id, name = l.Product.Name, qty = l.Qty, storeId = l.StoreId, storeName = l.StoreName, unitPrice = l.Unit, subtotal = l.Unit * l.Qty, storeChangedReason = l.Reason }),
            itemsTotal = sum, shipping = Shipping, estimatedTotal = total,
            individualBaselineTotal = baseline, estimatedSavings = baseline - total,
            formula = "예상 절감액 = (영업 중인 상점 평균가 × 수량 + 배송비 3,500원) − (선택 상점 가격 × 수량 + 배송비 3,500원)",
            assumptions = new[] { "모든 금액은 예상치이며 실제 결제가 없습니다.", "공동구매가 확정되면 배송비는 같은 구역 주문이 나눠 냅니다.", "승인 전에는 확정되지 않습니다." },
        };
    }

    public async Task<(int Status, object Body)> CreateAsync(Actor actor, CreateOrderInput input, string traceId)
    {
        var res = await db.Residents.AsNoTracking().FirstOrDefaultAsync(r => r.Id == actor.UserId)
            ?? throw ApiException.NotFound("주민을 찾을 수 없습니다.");
        var now = await clock.NowKstAsync();
        var (date, slot) = BatchSchedule.Next(now);
        var lines = await ResolveAsync(input.Items, date);
        var total = lines.Sum(l => l.Unit * l.Qty) + Shipping;

        if (input.Payment?.Simulate == "fail")
            return (402, new { code = "PAYMENT_DECLINED", message = "결제 승인이 거절되었습니다. 주문은 만들어지지 않았습니다.", traceId });

        await using var tx = await db.Database.BeginTransactionAsync();
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.BusinessDate == date && b.Slot == slot);
        if (batch is null)
        {
            batch = new Batch { BusinessDate = date, Slot = slot, CutoffAt = Time.KstToUtc(BatchSchedule.CutoffKst(date, slot)), Status = BatchStatus.Open };
            db.Batches.Add(batch);
            await db.SaveChangesAsync();
        }
        if (batch.Status != BatchStatus.Open) throw ApiException.Conflict("BATCH_CLOSED", "이 묶음은 이미 마감되었습니다.");

        var order = new Order { ResidentId = res.Id, ZoneId = res.ZoneId, BatchId = batch.Id, Status = OrderStatus.InBatch, EstimatedTotal = total, CreatedAt = DateTime.UtcNow };
        foreach (var l in lines)
            order.Items.Add(new OrderItem { ProductId = l.Product.Id, PreferredStoreId = null, StoreId = l.StoreId, Qty = l.Qty, EstimatedUnitPrice = l.Unit, Status = ItemStatus.Active, Source = l.Source, StoreChangedReason = l.Reason });
        for (var i = 0; i < order.Items.Count; i++) order.Items[i].PreferredStoreId = input.Items![i].PreferredStoreId;
        order.Payments.Add(new Payment { Kind = PaymentKind.Authorize, Amount = total, Status = PaymentStatus.Authorized, Reason = "모의 결제 승인", CreatedAt = DateTime.UtcNow });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (201, await DetailAsync(order.Id));
    }

    public async Task<object> DetailAsync(int id)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.Payments).FirstOrDefaultAsync(x => x.Id == id)
            ?? throw ApiException.NotFound("주문을 찾을 수 없습니다.");
        var pids = o.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var stores = await db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id);
        var batch = o.BatchId is int bid ? await db.Batches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid) : null;
        return new
        {
            id = o.Id, orderId = o.Id, residentId = o.ResidentId, zoneId = o.ZoneId, status = o.Status.ToString(),
            batchId = o.BatchId, batchStatus = batch?.Status.ToString(), batchLabel = batch is null ? null : $"{batch.BusinessDate:yyyy-MM-dd} {BatchSchedule.SlotLabel(batch.Slot)}",
            deliveryJobId = o.DeliveryJobId,
            estimatedTotal = o.EstimatedTotal, finalTotal = o.FinalTotal, feeShare = o.FeeShare,
            individualBaselineTotal = o.IndividualBaselineTotal, savings = o.Savings,
            createdAt = Time.Iso(o.CreatedAt),
            items = o.Items.OrderBy(i => i.Id).Select(i => new
            {
                id = i.Id, productId = i.ProductId, name = products[i.ProductId].Name, qty = i.Qty, status = i.Status.ToString(),
                storeId = i.StoreId, storeName = i.StoreId is int s ? stores[s].Name : null,
                estimatedUnitPrice = i.EstimatedUnitPrice, finalUnitPrice = i.FinalUnitPrice, storeChangedReason = i.StoreChangedReason,
            }),
            payments = o.Payments.OrderBy(p => p.Id).Select(p => new { id = p.Id, kind = p.Kind.ToString(), amount = p.Amount, status = p.Status.ToString(), reason = p.Reason }),
        };
    }

    public async Task<object> ListAsync(Actor actor)
    {
        var q = db.Orders.AsNoTracking().AsQueryable();
        if (actor.Role == "resident") q = q.Where(o => o.ResidentId == actor.UserId);
        var ids = await q.OrderByDescending(o => o.Id).Select(o => o.Id).Take(100).ToListAsync();
        var list = new List<object>();
        foreach (var id in ids) list.Add(await DetailAsync(id));
        return list;
    }

    public async Task<Order> OwnedAsync(Actor actor, int id)
    {
        var o = await db.Orders.Include(x => x.Items).Include(x => x.Payments).FirstOrDefaultAsync(x => x.Id == id)
            ?? throw ApiException.NotFound("주문을 찾을 수 없습니다.");
        if (actor.Role == "resident" && o.ResidentId != actor.UserId) throw ApiException.NotFound("주문을 찾을 수 없습니다.");
        return o;
    }

    public async Task<object> CancelAsync(Actor actor, int id)
    {
        var o = await OwnedAsync(actor, id);
        if (o.Status != OrderStatus.InBatch) throw ApiException.Conflict("NOT_CANCELLABLE", "이 주문은 취소할 수 없는 상태입니다.");
        var batch = await db.Batches.FirstAsync(b => b.Id == o.BatchId);
        var now = await clock.NowKstAsync();
        if (batch.Status != BatchStatus.Open || now >= Time.ToKst(batch.CutoffAt))
            throw ApiException.Conflict("CUTOFF_PASSED", "마감 시각이 지나 취소할 수 없습니다.");
        o.Status = OrderStatus.Cancelled;
        var auth = o.Payments.First(p => p.Kind == PaymentKind.Authorize);
        auth.Status = PaymentStatus.Settled;
        o.Payments.Add(new Payment { OrderId = o.Id, Kind = PaymentKind.Refund, Amount = auth.Amount, Status = PaymentStatus.Settled, Reason = "마감 전 취소 전액 환불", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return await DetailAsync(id);
    }

    public async Task<object> ApproveAdditionalAsync(Actor actor, int id)
    {
        var o = await OwnedAsync(actor, id);
        var pending = o.Payments.Where(p => p.Kind == PaymentKind.Additional && p.Status == PaymentStatus.Pending).ToList();
        if (pending.Count == 0) throw ApiException.Conflict("NO_PENDING_PAYMENT", "승인할 추가 결제가 없습니다.");
        foreach (var p in pending) p.Status = PaymentStatus.Settled;
        await db.SaveChangesAsync();
        return await DetailAsync(id);
    }

    public async Task<object> MetricsAsync()
    {
        var bound = await db.Orders.CountAsync(o => o.BatchId != null && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);
        var jobs = await db.DeliveryJobs.CountAsync();
        var settled = await db.Orders.Where(o => o.Savings != null).Select(o => o.Savings!.Value).ToListAsync();
        return new
        {
            ordersInBatches = bound, deliveryJobs = jobs, savedDeliveries = Math.Max(0, bound - jobs),
            storeOrders = await db.StoreOrders.CountAsync(),
            totalSavings = settled.Sum(), averageSavings = settled.Count == 0 ? 0 : (int)Math.Round(settled.Average()),
            note = "데모 규칙과 가상 좌표에 기반한 예상치입니다. 실측 결과가 아닙니다.",
        };
    }
}
