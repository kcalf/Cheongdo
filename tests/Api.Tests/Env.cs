using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace Api.Tests;

public class Env : WebApplicationFactory<Program>
{
    readonly string _db = Path.Combine(Path.GetTempPath(), $"chungdo-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("ConnectionStrings:Default", $"Data Source={_db}");
        b.UseSetting("OperatorKey", "test-key");
        b.UseSetting("Agent:StepDelayMs", "0");
    }

    public HttpRequestMessage Req(HttpMethod m, string url, string role, int id = 0, object? body = null, string? idem = null, bool opKey = true)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Demo-Role", role);
        r.Headers.Add("X-Demo-User-Id", id.ToString());
        if (role == "operator" && opKey) r.Headers.Add("X-Operator-Key", "test-key");
        if (idem is not null) r.Headers.Add("Idempotency-Key", idem);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    public async Task<(HttpStatusCode Status, JsonElement Json)> Call(HttpMethod m, string url, string role, int id = 0, object? body = null, string? idem = null, bool opKey = true)
    {
        var c = CreateClient();
        var res = await c.SendAsync(Req(m, url, role, id, body, idem, opKey));
        var text = await res.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (res.StatusCode, json);
    }

    public Task<(HttpStatusCode Status, JsonElement Json)> Op(HttpMethod m, string url, object? body = null) => Call(m, url, "operator", 0, body);

    public async Task Reset(string clock = "2026-11-09T10:00:00")
    {
        await Op(HttpMethod.Post, "/demo/reset", new { confirm = true });
        await Op(HttpMethod.Put, "/demo/clock", new { time = clock });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }
}
