using System.Text.Json;
using Api.Data;
using Api.Infra;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class BatchService(AppDb db, Settings settings)
{
    public async Task<object> ListAsync()
    {
        var list = await db.Batches.AsNoTracking().OrderByDescending(b => b.BusinessDate).ThenByDescending(b => b.Slot).Take(30).ToListAsync();
        var counts = await db.Orders.AsNoTracking().Where(o => o.BatchId != null).GroupBy(o => o.BatchId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return list.Select(b => new
        {
            id = b.Id, businessDate = b.BusinessDate.ToString("yyyy-MM-dd"), slot = b.Slot.ToString(), label = BatchSchedule.SlotLabel(b.Slot),
            cutoffAt = Time.Iso(b.CutoffAt), status = b.Status.ToString(), orderCount = counts.GetValueOrDefault(b.Id),
        });
    }

    public async Task<object> ConfirmAsync(DateOnly date, BatchSlot slot, string traceId)
    {
        var batch = await db.Batches.FirstOrDefaultAsync(b => b.BusinessDate == date && b.Slot == slot)
            ?? throw ApiException.NotFound("해당 묶음이 없습니다.");
        if (batch.Status is BatchStatus.Confirmed or BatchStatus.Delivered) return await SummaryAsync(batch.Id, "AlreadyConfirmed");
        if (batch.Status == BatchStatus.Confirming) throw ApiException.Conflict("BATCH_BUSY", "묶음을 확정하는 중입니다.");
        if (!await db.Orders.AnyAsync(o => o.BatchId == batch.Id && o.Status == OrderStatus.InBatch))
            return new { result = "NoOrders", batchId = batch.Id, status = batch.Status.ToString(), message = "묶을 주문이 없습니다." };

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await RunAsync(batch, date);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception e) when (e is not ApiException)
        {
            db.ChangeTracker.Clear();
            await db.Batches.Where(b => b.Id == batch.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BatchStatus.Failed));
            Audit.Add(db, "system", "BatchFailed", "Batch", batch.Id, new { error = e.Message }, traceId);
            await db.SaveChangesAsync();
            throw;
        }
        return await SummaryAsync(batch.Id, "Confirmed");
    }

    async Task RunAsync(Batch batch, DateOnly date)
    {
        var baseFee = await settings.GetIntAsync(SettingKeys.BaseFee, 3000);
        var perStop = await settings.GetIntAsync(SettingKeys.PerStopFee, 500);
        var earnKm = await settings.GetIntAsync(SettingKeys.EarnPerKm, 400);
        var earnBase = await settings.GetIntAsync(SettingKeys.EarnBase, 1500);
        var stores = await db.Stores.ToListAsync();
        var openIds = stores.Where(s => StoreCalendar.IsOpen(s.ClosedRule, date)).Select(s => s.Id).ToHashSet();
        var sps = await db.StoreProducts.ToListAsync();
        var orders = await db.Orders.Include(o => o.Items).Include(o => o.Payments)
            .Where(o => o.BatchId == batch.Id && o.Status == OrderStatus.InBatch).OrderBy(o => o.Id).ToListAsync();
        var now = DateTime.UtcNow;

        foreach (var o in orders)
        {
            foreach (var it in o.Items.OrderBy(i => i.Id))
            {
                var cands = sps.Where(sp => sp.ProductId == it.ProductId && openIds.Contains(sp.StoreId)).ToList();
                var inStock = cands.Where(sp => sp.StockQty >= it.Qty).OrderBy(sp => sp.Price).ThenBy(sp => sp.StoreId).ToList();
                var pick = it.PreferredStoreId is int pref ? inStock.FirstOrDefault(sp => sp.StoreId == pref) : null;
                string? reason = null;
                if (pick is null && inStock.Count > 0)
                {
                    pick = inStock[0];
                    if (it.PreferredStoreId is not null) reason = "선택한 상점의 재고가 부족해 최저가 상점으로 바꿨어요.";
                    else if (pick.StoreId != it.StoreId) reason = "재고가 바뀌어 다른 상점으로 바꿨어요.";
                }
                if (pick is null)
                {
                    it.Status = ItemStatus.Refunded;
                    it.StoreId = null;
                    it.StoreChangedReason = "재고가 없어 이 상품은 환불돼요.";
                    o.Payments.Add(new Payment { OrderId = o.Id, OrderItemId = it.Id, Kind = PaymentKind.Refund, Amount = it.EstimatedUnitPrice * it.Qty, Status = PaymentStatus.Settled, Reason = "재고 부족 환불", CreatedAt = now });
                    continue;
                }
                pick.StockQty -= it.Qty;
                it.StoreId = pick.StoreId;
                it.FinalUnitPrice = pick.Price;
                it.StoreChangedReason = reason ?? it.StoreChangedReason;
            }
        }

        var live = orders.Where(o => o.Items.Any(i => i.Status == ItemStatus.Active)).ToList();
        foreach (var o in orders.Except(live))
        {
            o.Status = OrderStatus.Refunded;
            var auth = o.Payments.First(p => p.Kind == PaymentKind.Authorize);
            auth.Status = PaymentStatus.Settled;
            var refunded = o.Payments.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount);
            var rest = auth.Amount - refunded;
            if (rest > 0) o.Payments.Add(new Payment { OrderId = o.Id, Kind = PaymentKind.Refund, Amount = rest, Status = PaymentStatus.Settled, Reason = "배송비 환불", CreatedAt = now });
        }

        var links = new List<(StoreOrder So, List<OrderItem> Items)>();
        foreach (var o in live)
        {
            foreach (var g in o.Items.Where(i => i.Status == ItemStatus.Active).GroupBy(i => i.StoreId!.Value))
            {
                var so = new StoreOrder { OrderId = o.Id, StoreId = g.Key, Status = StoreOrderStatus.Confirmed };
                db.StoreOrders.Add(so);
                links.Add((so, g.ToList()));
            }
        }
        await db.SaveChangesAsync();
        foreach (var (so, items) in links) foreach (var i in items) i.StoreOrderId = so.Id;

        var dist = await db.ZoneDistances.AsNoTracking().ToListAsync();
        var hub = stores.OrderBy(s => s.Id).First().ZoneId;
        foreach (var zone in live.GroupBy(o => o.ZoneId))
        {
            var zOrders = zone.OrderBy(o => o.Id).ToList();
            var fee = Fees.JobFee(baseFee, perStop, zOrders.Count);
            var shares = Fees.Split(fee, zOrders.Count);
            var m = dist.FirstOrDefault(d => d.FromZoneId == hub && d.ToZoneId == zone.Key)?.DistanceM ?? 4000;
            var job = new DeliveryJob
            {
                BatchId = batch.Id, ZoneId = zone.Key, Status = JobStatus.Open, StopCount = zOrders.Count, BaseFee = baseFee, PerStopFee = perStop,
                EstimatedDistanceM = m, EstimatedEarning = Fees.Earning(m, earnKm, earnBase),
                RouteOrderJson = JsonSerializer.Serialize(zOrders.Select(o => o.Id)),
            };
            db.DeliveryJobs.Add(job);
            await db.SaveChangesAsync();
            for (var i = 0; i < zOrders.Count; i++)
            {
                var o = zOrders[i];
                o.DeliveryJobId = job.Id;
                o.FeeShare = shares[i];
                o.Status = OrderStatus.Confirmed;
                var items = o.Items.Where(x => x.Status == ItemStatus.Active).ToList();
                var itemsTotal = items.Sum(x => x.FinalUnitPrice!.Value * x.Qty);
                o.FinalTotal = itemsTotal + shares[i];
                var baseline = items.Sum(x =>
                {
                    var prices = sps.Where(sp => sp.ProductId == x.ProductId && openIds.Contains(sp.StoreId)).Select(sp => sp.Price).ToList();
                    return (int)Math.Round(prices.Average()) * x.Qty;
                }) + OrderService.Shipping;
                o.IndividualBaselineTotal = baseline;
                o.Savings = baseline - o.FinalTotal;

                var auth = o.Payments.First(p => p.Kind == PaymentKind.Authorize);
                auth.Status = PaymentStatus.Settled;
                var refundedEst = o.Items.Where(x => x.Status == ItemStatus.Refunded).Sum(x => x.EstimatedUnitPrice * x.Qty);
                var delta = auth.Amount - refundedEst - o.FinalTotal.Value;
                if (delta > 0) o.Payments.Add(new Payment { OrderId = o.Id, Kind = PaymentKind.Refund, Amount = delta, Status = PaymentStatus.Settled, Reason = "확정 금액 차액 환불", CreatedAt = now });
                else if (delta < 0) o.Payments.Add(new Payment { OrderId = o.Id, Kind = PaymentKind.Additional, Amount = -delta, Status = PaymentStatus.Pending, Reason = "확정 금액 차액 추가 결제", CreatedAt = now });
            }
            foreach (var so in db.StoreOrders.Local.Where(s => zOrders.Any(o => o.Id == s.OrderId))) so.DeliveryJobId = job.Id;
        }

        batch.Status = BatchStatus.Confirmed;
        batch.ConfirmedAt = now;
        Audit.Add(db, "system", "BatchConfirmed", "Batch", batch.Id, new { orders = live.Count, refundedOrders = orders.Count - live.Count }, "batch");
    }

    public async Task<object> SummaryAsync(int batchId, string result)
    {
        var b = await db.Batches.AsNoTracking().FirstAsync(x => x.Id == batchId);
        var jobs = await db.DeliveryJobs.AsNoTracking().Where(j => j.BatchId == batchId).OrderBy(j => j.Id).ToListAsync();
        var orders = await db.Orders.AsNoTracking().Where(o => o.BatchId == batchId).OrderBy(o => o.Id).ToListAsync();
        return new
        {
            result, batchId, status = b.Status.ToString(),
            orders = orders.Select(o => new { id = o.Id, status = o.Status.ToString(), finalTotal = o.FinalTotal, feeShare = o.FeeShare, savings = o.Savings, deliveryJobId = o.DeliveryJobId }),
            jobs = jobs.Select(j => new { id = j.Id, zoneId = j.ZoneId, status = j.Status.ToString(), stopCount = j.StopCount, fee = Fees.JobFee(j.BaseFee, j.PerStopFee, j.StopCount) }),
            totalSavings = orders.Sum(o => o.Savings ?? 0),
        };
    }
}
