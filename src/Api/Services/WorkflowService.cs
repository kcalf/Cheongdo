using System.Text.Json;
using Api.Data;
using Api.Infra;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public record ProductInput(string? Name, string? Unit, int Price, int StockQty);
public record StockInput(int Price, int StockQty);

public class WorkflowService(AppDb db)
{
    public async Task<object> StoreOrdersAsync(Actor actor)
    {
        var q = db.StoreOrders.AsNoTracking().AsQueryable();
        if (actor.Role == "store") q = q.Where(s => s.StoreId == actor.UserId);
        var sos = await q.OrderByDescending(s => s.Id).Take(100).ToListAsync();
        var ids = sos.Select(s => s.Id).ToList();
        var items = await db.OrderItems.AsNoTracking().Where(i => i.StoreOrderId != null && ids.Contains(i.StoreOrderId.Value)).ToListAsync();
        var pids = items.Select(i => i.ProductId).Distinct().ToList();
        var names = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return sos.Select(s => new
        {
            id = s.Id, orderId = s.OrderId, storeId = s.StoreId, status = s.Status.ToString(), deliveryJobId = s.DeliveryJobId,
            items = items.Where(i => i.StoreOrderId == s.Id).Select(i => new { name = names[i.ProductId], qty = i.Qty, unitPrice = i.FinalUnitPrice }),
        });
    }

    public async Task<object> StoreStepAsync(Actor actor, int id, StoreOrderStatus to)
    {
        var so = await db.StoreOrders.FirstOrDefaultAsync(s => s.Id == id && s.StoreId == actor.UserId)
            ?? throw ApiException.NotFound("상점 주문을 찾을 수 없습니다.");
        if (!Transitions.Can(so.Status, to)) throw ApiException.Conflict("INVALID_TRANSITION", $"{so.Status} 상태에서 {to}(으)로 바꿀 수 없습니다.");
        so.Status = to;
        var order = await db.Orders.FirstAsync(o => o.Id == so.OrderId);
        var siblings = await db.StoreOrders.Where(s => s.OrderId == so.OrderId && s.Id != so.Id).Select(s => s.Status).ToListAsync();
        var all = siblings.Append(to).ToList();
        var next = all.All(s => s == StoreOrderStatus.Ready) ? OrderStatus.ReadyForPickup
            : all.Any(s => s != StoreOrderStatus.Confirmed) ? OrderStatus.Preparing : OrderStatus.Confirmed;
        if (next != order.Status && (order.Status is OrderStatus.Confirmed or OrderStatus.Preparing)) order.Status = next;
        await db.SaveChangesAsync();
        return new { id = so.Id, status = so.Status.ToString(), orderStatus = order.Status.ToString() };
    }

    public async Task<object> StoreProductsAsync(Actor actor)
    {
        var mine = await db.StoreProducts.AsNoTracking().Where(sp => sp.StoreId == actor.UserId).ToDictionaryAsync(sp => sp.ProductId);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        return products.Select(p => new { productId = p.Id, name = p.Name, unit = p.Unit, price = mine.GetValueOrDefault(p.Id)?.Price ?? 0, stockQty = mine.GetValueOrDefault(p.Id)?.StockQty ?? 0 });
    }

    static void CheckStock(int price, int stock)
    {
        if (price < 0 || price > 1_000_000) throw ApiException.BadRequest("INVALID_PRICE", "가격은 0~1,000,000원입니다.");
        if (stock < 0 || stock > 100_000) throw ApiException.BadRequest("INVALID_STOCK", "재고는 0~100,000개입니다.");
    }

    public async Task<object> UpsertStockAsync(Actor actor, int productId, StockInput input)
    {
        CheckStock(input.Price, input.StockQty);
        if (!await db.Products.AnyAsync(p => p.Id == productId)) throw ApiException.NotFound("상품을 찾을 수 없습니다.");
        var sp = await db.StoreProducts.FirstOrDefaultAsync(x => x.StoreId == actor.UserId && x.ProductId == productId);
        if (sp is null) db.StoreProducts.Add(sp = new StoreProduct { StoreId = actor.UserId, ProductId = productId });
        sp.Price = input.Price;
        sp.StockQty = input.StockQty;
        await db.SaveChangesAsync();
        return new { productId, price = sp.Price, stockQty = sp.StockQty };
    }

    public async Task<object> AddProductAsync(Actor actor, ProductInput input)
    {
        var name = input.Name?.Trim() ?? "";
        var unit = input.Unit?.Trim() ?? "";
        if (name.Length is 0 or > 40) throw ApiException.BadRequest("INVALID_NAME", "상품 이름은 1~40자입니다.");
        if (unit.Length is 0 or > 10) throw ApiException.BadRequest("INVALID_UNIT", "단위는 1~10자입니다.");
        CheckStock(input.Price, input.StockQty);
        if (!await db.Stores.AnyAsync(s => s.Id == actor.UserId)) throw ApiException.NotFound("상점을 찾을 수 없습니다.");
        if (await db.Products.AnyAsync(p => p.Name == name)) throw ApiException.Conflict("DUPLICATE_PRODUCT", "이미 있는 상품입니다.");
        var p = new Product { Name = name, Unit = unit, Category = "기타", Keywords = name };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        db.StoreProducts.Add(new StoreProduct { StoreId = actor.UserId, ProductId = p.Id, Price = input.Price, StockQty = input.StockQty });
        await db.SaveChangesAsync();
        return new { productId = p.Id, name, unit, price = input.Price, stockQty = input.StockQty };
    }

    public async Task<object> JobsAsync(Actor actor)
    {
        var q = db.DeliveryJobs.AsNoTracking().AsQueryable();
        if (actor.Role == "courier") q = q.Where(j => j.Status == JobStatus.Open || j.AcceptedByCourierId == actor.UserId);
        var jobs = await q.OrderByDescending(j => j.Id).Take(100).ToListAsync();
        var zones = await db.Zones.AsNoTracking().ToDictionaryAsync(z => z.Id, z => z.Name);
        var stores = await db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var ids = jobs.Select(j => j.Id).ToList();
        var sos = await db.StoreOrders.AsNoTracking().Where(s => s.DeliveryJobId != null && ids.Contains(s.DeliveryJobId.Value)).ToListAsync();
        return jobs.Select(j =>
        {
            var mine = sos.Where(s => s.DeliveryJobId == j.Id).ToList();
            return new
            {
                id = j.Id, batchId = j.BatchId, zoneId = j.ZoneId, zoneName = zones[j.ZoneId], status = j.Status.ToString(),
                acceptedByCourierId = j.AcceptedByCourierId, stopCount = j.StopCount,
                fee = Fees.JobFee(j.BaseFee, j.PerStopFee, j.StopCount),
                estimatedDistanceM = j.EstimatedDistanceM, estimatedEarning = j.EstimatedEarning,
                route = JsonSerializer.Deserialize<int[]>(j.RouteOrderJson),
                pickups = mine.GroupBy(s => s.StoreId).Select(g => new { storeId = g.Key, storeName = stores[g.Key], ready = g.All(s => s.Status == StoreOrderStatus.Ready) }),
                allReady = mine.Count > 0 && mine.All(s => s.Status == StoreOrderStatus.Ready),
                note = "거리와 수익은 데모 규칙과 가상 좌표 기반 예상치입니다.",
            };
        });
    }

    public async Task<object> AcceptAsync(Actor actor, int id)
    {
        if (!await db.Couriers.AnyAsync(c => c.Id == actor.UserId)) throw ApiException.NotFound("배송자를 찾을 수 없습니다.");
        var job = await db.DeliveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id) ?? throw ApiException.NotFound("작업을 찾을 수 없습니다.");
        if (await db.DeliveryJobs.AnyAsync(j => j.AcceptedByCourierId == actor.UserId && j.Status != JobStatus.Delivered))
            throw ApiException.Conflict("ALREADY_ACTIVE", "진행 중인 작업을 먼저 끝내 주세요.");
        var sos = await db.StoreOrders.AsNoTracking().Where(s => s.DeliveryJobId == id).ToListAsync();
        if (sos.Count == 0 || sos.Any(s => s.Status != StoreOrderStatus.Ready))
            throw ApiException.Conflict("NOT_READY", "모든 상점의 준비가 끝나야 수락할 수 있습니다.");
        var n = await db.DeliveryJobs.Where(j => j.Id == id && j.Status == JobStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Accepted).SetProperty(j => j.AcceptedByCourierId, actor.UserId));
        if (n == 0) throw ApiException.Conflict("JOB_TAKEN", "다른 배송자가 먼저 수락했습니다.");
        return new { id, status = "Accepted", acceptedByCourierId = actor.UserId };
    }

    public async Task<object> AdvanceJobAsync(Actor actor, int id, JobStatus to)
    {
        var job = await db.DeliveryJobs.FirstOrDefaultAsync(j => j.Id == id) ?? throw ApiException.NotFound("작업을 찾을 수 없습니다.");
        if (job.AcceptedByCourierId != actor.UserId) throw ApiException.Forbidden("수락한 배송자만 상태를 바꿀 수 있습니다.");
        if (!Transitions.Can(job.Status, to)) throw ApiException.Conflict("INVALID_TRANSITION", $"{job.Status} 상태에서 {to}(으)로 바꿀 수 없습니다.");
        job.Status = to;
        var orders = await db.Orders.Where(o => o.DeliveryJobId == id).ToListAsync();
        var target = to == JobStatus.PickedUp ? OrderStatus.PickedUp : OrderStatus.Delivered;
        foreach (var o in orders.Where(o => Transitions.Can(o.Status, target))) o.Status = target;
        if (to == JobStatus.Delivered
            && !await db.DeliveryJobs.AnyAsync(j => j.BatchId == job.BatchId && j.Id != id && j.Status != JobStatus.Delivered))
        {
            var batch = await db.Batches.FirstAsync(b => b.Id == job.BatchId);
            batch.Status = BatchStatus.Delivered;
        }
        await db.SaveChangesAsync();
        return new { id, status = to.ToString() };
    }
}
