using System.Net;
using System.Text.Json;
using Xunit;

namespace Api.Tests;

public class ApiFlowTests : IClassFixture<Env>
{
    readonly Env e;
    public ApiFlowTests(Env env) => e = env;

    static object Items(params (int p, int q, int? s)[] xs) =>
        new { items = xs.Select(x => new { productId = x.p, qty = x.q, preferredStoreId = x.s }), payment = new { simulate = "approve" } };

    [Fact]
    public async Task Role_and_key_are_enforced()
    {
        var (s, _) = await e.Call(HttpMethod.Get, "/orders", "store", 1);
        Assert.Equal(HttpStatusCode.Forbidden, s);
        (s, _) = await e.Call(HttpMethod.Post, "/demo/reset", "operator", 0, new { confirm = true }, opKey: false);
        Assert.Equal(HttpStatusCode.Forbidden, s);
        (s, _) = await e.Call(HttpMethod.Get, "/health", "resident");
        Assert.Equal(HttpStatusCode.OK, s);
        (s, var j) = await e.Op(HttpMethod.Get, "/demo/audit-logs");
        Assert.Contains("OperatorKeyRejected", j.ToString());
    }

    [Fact]
    public async Task Full_flow_two_orders_one_zone_share_fee()
    {
        await e.Reset();
        var (s1, o1) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((1, 2, null)), "k1");
        var (s2, o2) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((4, 1, null)), "k2");
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.Equal("InBatch", o1.GetProperty("status").GetString());

        var (_, run) = await e.Op(HttpMethod.Post, "/demo/batches/run", new { businessDate = "2026-11-09", slot = "PM15", confirm = true });
        Assert.Equal("Confirmed", run.GetProperty("result").GetString());
        Assert.Single(run.GetProperty("jobs").EnumerateArray());
        var fees = run.GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("feeShare").GetInt32()).ToArray();
        Assert.Equal(4000, fees.Sum()); // 3000 + 500 × 2

        var (_, again) = await e.Op(HttpMethod.Post, "/demo/batches/run", new { businessDate = "2026-11-09", slot = "PM15", confirm = true });
        Assert.Equal("AlreadyConfirmed", again.GetProperty("result").GetString());

        var (_, sos) = await e.Call(HttpMethod.Get, "/store/orders", "store", 2);
        var job = run.GetProperty("jobs")[0].GetProperty("id").GetInt32();
        var (acc, _) = await e.Call(HttpMethod.Post, $"/courier/jobs/{job}/accept", "courier", 1);
        Assert.Equal(HttpStatusCode.Conflict, acc);

        foreach (var uid in new[] { 1, 2, 3 })
        {
            var (_, list) = await e.Call(HttpMethod.Get, "/store/orders", "store", uid);
            foreach (var so in list.EnumerateArray())
            {
                var id = so.GetProperty("id").GetInt32();
                Assert.Equal(HttpStatusCode.OK, (await e.Call(HttpMethod.Post, $"/store/orders/{id}/prepare", "store", uid)).Status);
                Assert.Equal(HttpStatusCode.OK, (await e.Call(HttpMethod.Post, $"/store/orders/{id}/ready", "store", uid)).Status);
            }
        }
        Assert.Equal(HttpStatusCode.OK, (await e.Call(HttpMethod.Post, $"/courier/jobs/{job}/accept", "courier", 1)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await e.Call(HttpMethod.Post, $"/courier/jobs/{job}/accept", "courier", 2)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Call(HttpMethod.Post, $"/courier/jobs/{job}/pickup", "courier", 2)).Status);
        Assert.Equal(HttpStatusCode.OK, (await e.Call(HttpMethod.Post, $"/courier/jobs/{job}/pickup", "courier", 1)).Status);
        Assert.Equal(HttpStatusCode.OK, (await e.Call(HttpMethod.Post, $"/courier/jobs/{job}/deliver", "courier", 1)).Status);

        var oid = o1.GetProperty("orderId").GetInt32();
        var (_, d) = await e.Call(HttpMethod.Get, $"/orders/{oid}", "resident", 1);
        Assert.Equal("Delivered", d.GetProperty("status").GetString());
        Assert.Equal("Delivered", d.GetProperty("batchStatus").GetString());
        var (_, m) = await e.Op(HttpMethod.Get, "/demo/metrics");
        Assert.Equal(1, m.GetProperty("savedDeliveries").GetInt32());
    }

    [Fact]
    public async Task Idempotent_create_replays_and_mismatch_conflicts()
    {
        await e.Reset();
        var (_, a) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((1, 1, null)), "same");
        var (st, b) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((1, 1, null)), "same");
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.Equal(a.GetProperty("orderId").GetInt32(), b.GetProperty("orderId").GetInt32());
        var (c, _) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((1, 5, null)), "same");
        Assert.Equal(HttpStatusCode.Conflict, c);
        var (_, list) = await e.Call(HttpMethod.Get, "/orders", "resident", 1);
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Payment_failure_creates_no_order()
    {
        await e.Reset();
        var body = new { items = new[] { new { productId = 1, qty = 1, preferredStoreId = (int?)null } }, payment = new { simulate = "fail" } };
        var (s, j) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, body);
        Assert.Equal(HttpStatusCode.PaymentRequired, s);
        Assert.Equal("PAYMENT_DECLINED", j.GetProperty("code").GetString());
        var (_, list) = await e.Call(HttpMethod.Get, "/orders", "resident", 1);
        Assert.Empty(list.EnumerateArray());
    }

    [Fact]
    public async Task Closed_store_is_replaced_and_preferred_store_is_used_when_open()
    {
        await e.Reset("2026-11-08T10:00:00");
        var (_, p) = await e.Call(HttpMethod.Post, "/orders/preview", "resident", 1, new { items = new[] { new { productId = 1, qty = 1, preferredStoreId = (int?)1 } } });
        var line = p.GetProperty("items")[0];
        Assert.NotEqual(1, line.GetProperty("storeId").GetInt32());
        Assert.False(string.IsNullOrEmpty(line.GetProperty("storeChangedReason").GetString()));

        await e.Reset("2026-11-09T10:00:00");
        (_, p) = await e.Call(HttpMethod.Post, "/orders/preview", "resident", 1, new { items = new[] { new { productId = 1, qty = 1, preferredStoreId = (int?)1 } } });
        Assert.Equal(1, p.GetProperty("items")[0].GetProperty("storeId").GetInt32());
        Assert.Equal(2200 + 3500, p.GetProperty("estimatedTotal").GetInt32());
    }

    [Fact]
    public async Task Stock_shortage_refunds_item_when_batch_runs()
    {
        await e.Reset();
        var (s1, o1) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((6, 8, null), (1, 1, null)));
        var (s2, o2) = await e.Call(HttpMethod.Post, "/orders", "resident", 2, Items((6, 8, null)));
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
        await e.Op(HttpMethod.Post, "/demo/batches/run", new { businessDate = "2026-11-09", slot = "PM15", confirm = true });
        var (_, d1) = await e.Call(HttpMethod.Get, $"/orders/{o1.GetProperty("orderId").GetInt32()}", "resident", 1);
        var (_, d2) = await e.Call(HttpMethod.Get, $"/orders/{o2.GetProperty("orderId").GetInt32()}", "resident", 2);
        Assert.Equal("Confirmed", d1.GetProperty("status").GetString());
        Assert.Equal("Refunded", d2.GetProperty("status").GetString());
        var refund = d2.GetProperty("payments").EnumerateArray().Where(p => p.GetProperty("kind").GetString() == "Refund").Sum(p => p.GetProperty("amount").GetInt32());
        Assert.Equal(d2.GetProperty("estimatedTotal").GetInt32(), refund);
    }

    [Fact]
    public async Task Cancel_after_cutoff_fails()
    {
        await e.Reset();
        var (_, o) = await e.Call(HttpMethod.Post, "/orders", "resident", 3, Items((4, 1, null)));
        await e.Op(HttpMethod.Put, "/demo/clock", new { time = "2026-11-09T14:31:00" });
        var (c, j) = await e.Call(HttpMethod.Post, $"/orders/{o.GetProperty("orderId").GetInt32()}/cancel", "resident", 3);
        Assert.Equal(HttpStatusCode.Conflict, c);
        Assert.Equal("CUTOFF_PASSED", j.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_before_cutoff_refunds_in_full()
    {
        await e.Reset();
        var (_, o) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((4, 1, null)));
        var oid = o.GetProperty("orderId").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await e.Call(HttpMethod.Post, $"/orders/{oid}/cancel", "resident", 1)).Status);
        var (_, d) = await e.Call(HttpMethod.Get, $"/orders/{oid}", "resident", 1);
        Assert.Equal("Cancelled", d.GetProperty("status").GetString());
        Assert.Contains(d.GetProperty("payments").EnumerateArray(), p => p.GetProperty("kind").GetString() == "Refund");
    }

    [Fact]
    public async Task Resident_cannot_see_other_residents_order()
    {
        await e.Reset();
        var (_, o) = await e.Call(HttpMethod.Post, "/orders", "resident", 1, Items((4, 1, null)));
        var (s, _) = await e.Call(HttpMethod.Get, $"/orders/{o.GetProperty("orderId").GetInt32()}", "resident", 2);
        Assert.Equal(HttpStatusCode.NotFound, s);
    }

    [Fact]
    public async Task Store_can_add_product_and_duplicates_conflict()
    {
        await e.Reset();
        var body = new { name = "참기름", unit = "병", price = 6500, stockQty = 5 };
        var (s, j) = await e.Call(HttpMethod.Post, "/store/products", "store", 2, body);
        Assert.Equal(HttpStatusCode.Created, s);
        Assert.Equal(6500, j.GetProperty("price").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await e.Call(HttpMethod.Post, "/store/products", "store", 2, body)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Call(HttpMethod.Post, "/store/products", "store", 2, new { name = "", unit = "병", price = 1, stockQty = 1 })).Status);
        var (_, mine) = await e.Call(HttpMethod.Get, "/store/products", "store", 3);
        Assert.Equal(0, mine.EnumerateArray().First(p => p.GetProperty("name").GetString() == "참기름").GetProperty("stockQty").GetInt32());
    }

    [Fact]
    public async Task Agent_run_streams_draft_and_respects_ai_off()
    {
        await e.Reset();
        var (s, r) = await e.Call(HttpMethod.Post, "/agent-runs", "resident", 1, new { text = "두부 두 모랑 라면 3봉, 망고" });
        Assert.Equal(HttpStatusCode.Accepted, s);
        var id = r.GetProperty("runId").GetInt32();
        var res = await e.CreateClient().SendAsync(e.Req(HttpMethod.Get, $"/agent-runs/{id}/events", "resident", 1));
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("event: draft", text);
        Assert.Contains("\"aiGenerated\":true", text);
        Assert.Contains("notfound", text);
        Assert.Contains("event: done", text);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Call(HttpMethod.Post, $"/agent-runs/{id}/cancel", "resident", 2)).Status);

        await e.Op(HttpMethod.Put, "/demo/ai", new { enabled = false });
        (_, r) = await e.Call(HttpMethod.Post, "/agent-runs", "resident", 1, new { text = "두부 1모" });
        res = await e.CreateClient().SendAsync(e.Req(HttpMethod.Get, $"/agent-runs/{r.GetProperty("runId").GetInt32()}/events", "resident", 1));
        Assert.Contains("AI_UNAVAILABLE", await res.Content.ReadAsStringAsync());
        await e.Op(HttpMethod.Put, "/demo/ai", new { enabled = true });
    }
}
