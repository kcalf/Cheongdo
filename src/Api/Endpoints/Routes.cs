using Api.Data;
using Api.Infra;
using Api.Services;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public record ClockInput(string? Time);
public record AiInput(bool Enabled);
public record ConfirmInput(bool Confirm);
public record RunBatchInput(string? BusinessDate, string? Slot, bool Confirm);

public static class Routes
{
    static RoleFilter R(params string[] roles) => new(roles);

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", async (AppDb db) =>
            await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ok" }) : Results.Json(new { status = "db_unavailable" }, statusCode: 503));

        var all = app.MapGroup("").AddEndpointFilter(R("resident", "store", "courier", "operator"));
        all.MapGet("/batches", (BatchService s) => s.ListAsync());
        all.MapGet("/products", async (AppDb db, string? query, int? storeId) =>
        {
            var q = db.Products.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(query)) q = q.Where(p => p.Name.Contains(query) || p.Keywords.Contains(query));
            var products = await q.OrderBy(p => p.Id).ToListAsync();
            var ids = products.Select(p => p.Id).ToList();
            var sps = await db.StoreProducts.AsNoTracking().Where(sp => ids.Contains(sp.ProductId) && (storeId == null || sp.StoreId == storeId)).ToListAsync();
            var stores = await db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
            return products.Select(p => new
            {
                productId = p.Id, name = p.Name, unit = p.Unit,
                stores = sps.Where(sp => sp.ProductId == p.Id).OrderBy(sp => sp.Price).Select(sp => new { storeId = sp.StoreId, storeName = stores[sp.StoreId], price = sp.Price, stockQty = sp.StockQty }),
            });
        });

        var idem = new IdempotencyFilter();
        all.MapGet("/clock", async (IClock c, Settings s) => new
        {
            nowKst = (await c.NowKstAsync()).ToString("yyyy-MM-ddTHH:mm:ss"),
            demoClock = await s.GetAsync(SettingKeys.DemoClock),
            aiEnabled = !bool.TryParse(await s.GetAsync(SettingKeys.AiEnabled), out var on) || on,
        });
        var resident = app.MapGroup("").AddEndpointFilter(R("resident"));
        var residentRead = app.MapGroup("").AddEndpointFilter(R("resident", "operator"));
        residentRead.MapGet("/orders", (HttpContext c, OrderService s) => s.ListAsync(c.Actor()));
        residentRead.MapGet("/orders/{id:int}", async (HttpContext c, OrderService s, int id) => { await s.OwnedAsync(c.Actor(), id); return await s.DetailAsync(id); });
        resident.MapPost("/orders/preview", (OrderService s, PreviewInput i) => s.PreviewAsync(i));
        resident.MapPost("/orders", async (HttpContext c, OrderService s, CreateOrderInput i) =>
        {
            var (status, body) = await s.CreateAsync(c.Actor(), i, c.TraceIdentifier);
            return Results.Json(body, statusCode: status);
        }).AddEndpointFilter(idem);
        resident.MapPost("/orders/{id:int}/cancel", async (HttpContext c, OrderService s, int id) => { await s.CancelAsync(c.Actor(), id); return Results.NoContent(); });
        resident.MapPost("/orders/{id:int}/payments/additional", (HttpContext c, OrderService s, int id) => s.ApproveAdditionalAsync(c.Actor(), id)).AddEndpointFilter(idem);

        resident.MapPost("/agent-runs", async (HttpContext c, AgentService s, AgentRunInput i) =>
            Results.Json(new { runId = await s.StartAsync(c.Actor(), i) }, statusCode: 202));
        resident.MapGet("/agent-runs/{id:int}/events", (HttpContext c, AgentService s, int id) => s.StreamAsync(c, id));
        resident.MapPost("/agent-runs/{id:int}/cancel", async (HttpContext c, AgentService s, int id) => { await s.CancelAsync(c.Actor(), id); return Results.NoContent(); });

        var store = app.MapGroup("/store").AddEndpointFilter(R("store", "operator"));
        store.MapGet("/orders", (HttpContext c, WorkflowService s) => s.StoreOrdersAsync(c.Actor()));
        store.MapGet("/products", (HttpContext c, WorkflowService s) => s.StoreProductsAsync(c.Actor()));
        var storeWrite = app.MapGroup("/store").AddEndpointFilter(R("store"));
        storeWrite.MapPost("/orders/{id:int}/prepare", (HttpContext c, WorkflowService s, int id) => s.StoreStepAsync(c.Actor(), id, StoreOrderStatus.Preparing));
        storeWrite.MapPost("/orders/{id:int}/ready", (HttpContext c, WorkflowService s, int id) => s.StoreStepAsync(c.Actor(), id, StoreOrderStatus.Ready));
        storeWrite.MapPut("/products/{productId:int}", (HttpContext c, WorkflowService s, int productId, StockInput i) => s.UpsertStockAsync(c.Actor(), productId, i));
        storeWrite.MapPost("/products", async (HttpContext c, WorkflowService s, ProductInput i) => Results.Json(await s.AddProductAsync(c.Actor(), i), statusCode: 201)).AddEndpointFilter(idem);

        var courierRead = app.MapGroup("/courier").AddEndpointFilter(R("courier", "operator"));
        courierRead.MapGet("/jobs", async (HttpContext c, WorkflowService s, string? status) =>
        {
            var jobs = (await s.JobsAsync(c.Actor())) as IEnumerable<dynamic>;
            return status is null ? jobs : jobs!.Where(j => (string)j.status == status);
        });
        var courier = app.MapGroup("/courier").AddEndpointFilter(R("courier"));
        courier.MapPost("/jobs/{id:int}/accept", (HttpContext c, WorkflowService s, int id) => s.AcceptAsync(c.Actor(), id));
        courier.MapPost("/jobs/{id:int}/pickup", (HttpContext c, WorkflowService s, int id) => s.AdvanceJobAsync(c.Actor(), id, JobStatus.PickedUp));
        courier.MapPost("/jobs/{id:int}/deliver", (HttpContext c, WorkflowService s, int id) => s.AdvanceJobAsync(c.Actor(), id, JobStatus.Delivered));

        var demo = app.MapGroup("/demo").AddEndpointFilter(R("operator"));
        demo.MapPost("/batches/run", async (HttpContext c, BatchService s, RunBatchInput i) =>
        {
            if (!i.Confirm) throw ApiException.BadRequest("CONFIRM_REQUIRED", "confirm:true가 필요합니다.");
            if (!DateOnly.TryParse(i.BusinessDate, out var date) || !Enum.TryParse<BatchSlot>(i.Slot, out var slot))
                throw ApiException.BadRequest("INVALID_BATCH", "businessDate(yyyy-MM-dd)와 slot(AM09|PM15)이 필요합니다.");
            return await s.ConfirmAsync(date, slot, c.TraceIdentifier);
        });
        demo.MapPut("/clock", async (Settings s, ClockInput i) =>
        {
            if (i.Time is not null && !DateTime.TryParse(i.Time, out _)) throw ApiException.BadRequest("INVALID_CLOCK", "시각 형식이 올바르지 않습니다.");
            await s.SetAsync(SettingKeys.DemoClock, i.Time);
            return Results.Ok(new { time = i.Time });
        });
        demo.MapPut("/ai", async (Settings s, AiInput i) => { await s.SetAsync(SettingKeys.AiEnabled, i.Enabled.ToString()); return Results.Ok(new { enabled = i.Enabled }); });
        demo.MapPost("/reset", async (HttpContext c, AppDb db, ConfirmInput i) =>
        {
            if (!i.Confirm) throw ApiException.BadRequest("CONFIRM_REQUIRED", "confirm:true가 필요합니다.");
            await Seeder.ResetAsync(db);
            Audit.Add(db, "operator", "DemoReset", "System", "all", null, c.TraceIdentifier);
            await db.SaveChangesAsync();
            return Results.Ok(new { reset = true });
        });
        demo.MapGet("/metrics", (OrderService s) => s.MetricsAsync());
        demo.MapGet("/audit-logs", async (AppDb db) => await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(100)
            .Select(a => new { a.Id, a.ActorRole, a.Action, a.TargetType, a.TargetId, a.DetailJson, a.TraceId, a.CreatedAt }).ToListAsync());
    }
}
