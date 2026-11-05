using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Infra;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public record InterpretedItem(string Text, int? ProductId, string? ProductName, int Qty, string Status, string? Question);

// Copilot SDK / Agent Framework 어댑터가 같은 인터페이스를 구현해 교체할 수 있다.
public interface IOrderInterpreter
{
    Task<List<InterpretedItem>> InterpretAsync(string input, IReadOnlyList<Product> products, CancellationToken ct, Func<string, object, Task>? onEvent = null);
}

public class KeywordInterpreter : IOrderInterpreter
{
    static readonly Dictionary<string, int> KNum = new()
    {
        ["한"] = 1, ["두"] = 2, ["세"] = 3, ["네"] = 4, ["다섯"] = 5, ["여섯"] = 6, ["일곱"] = 7, ["여덟"] = 8, ["아홉"] = 9, ["열"] = 10,
    };
    static readonly Regex Split = new(@"[,\n]|(?:이랑|랑|하고)(?=\s|$)|\s(?:그리고|및)\s", RegexOptions.Compiled);
    static readonly Regex Wa = new(@"[와과](?=\s)", RegexOptions.Compiled);
    static readonly Regex Qty = new(@"^\s+(?:\d+|한|두|세|네|다섯|여섯|일곱|여덟|아홉|열)(?=\s|[개모단판봉포병팩망]|kg|L)", RegexOptions.Compiled);

    // "사과 3개"처럼 와/과가 이름의 일부이면 자르지 않는다.
    static IEnumerable<string> SplitItems(string input, IReadOnlyList<Product> products)
    {
        var keys = products.SelectMany(p => p.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries)).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        foreach (var part in Split.Split(input))
        {
            var start = 0;
            foreach (Match m in Wa.Matches(part))
            {
                var upto = part[..(m.Index + 1)];
                if (keys.Any(k => upto.EndsWith(k)) || Qty.IsMatch(part[(m.Index + 1)..])) continue;
                yield return part[start..m.Index];
                start = m.Index + 1;
            }
            yield return part[start..];
        }
    }

    public async Task<List<InterpretedItem>> InterpretAsync(string input, IReadOnlyList<Product> products, CancellationToken ct, Func<string, object, Task>? onEvent = null)
    {
        var result = new List<InterpretedItem>();
        foreach (var raw in SplitItems(input, products))
        {
            var text = raw.Trim();
            if (text.Length == 0) continue;
            var hit = products
                .Select(p => (P: p, Len: p.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(k => text.Contains(k.Trim())).Select(k => k.Trim().Length).DefaultIfEmpty(0).Max()))
                .Where(x => x.Len > 0).OrderByDescending(x => x.Len).Select(x => x.P).FirstOrDefault();
            var digit = Regex.Match(text, @"(\d+)");
            var kor = KNum.Keys.FirstOrDefault(k => text.Contains(k));
            var qty = digit.Success ? int.Parse(digit.Groups[1].Value) : kor is not null ? KNum[kor] : 0;
            if (hit is null) result.Add(new InterpretedItem(text, null, null, qty == 0 ? 1 : Math.Min(qty, 99), "notfound", "카탈로그에 없는 상품입니다."));
            else if (qty == 0) result.Add(new InterpretedItem(text, hit.Id, hit.Name, 1, "ambiguous", "수량이 없어 1로 가정했어요. 맞는지 확인해 주세요."));
            else result.Add(new InterpretedItem(text, hit.Id, hit.Name, Math.Min(qty, 99), "ok", null));
        }
        if (onEvent is not null)
            foreach (var i in result) await onEvent("tool_call", new { tool = "catalog.search", input = i.Text, matched = i.ProductName });
        return result;
    }
}

public record AgentRunInput(string? Text);

public class AgentService(IServiceScopeFactory scopes, AppDb db, IConfiguration cfg)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> StartAsync(Actor actor, AgentRunInput input)
    {
        var text = input.Text?.Trim() ?? "";
        if (text.Length is 0 or > 500) throw ApiException.BadRequest("INVALID_TEXT", "주문 내용은 1~500자로 입력해 주세요.");
        if (!await db.Residents.AnyAsync(r => r.Id == actor.UserId)) throw ApiException.NotFound("주민을 찾을 수 없습니다.");
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var oldIds = await db.AgentRuns.Where(r => r.CreatedAt < cutoff).Select(r => r.Id).ToListAsync();
        if (oldIds.Count > 0)
        {
            await db.AgentEvents.Where(e => oldIds.Contains(e.RunId)).ExecuteDeleteAsync();
            await db.AgentRuns.Where(r => oldIds.Contains(r.Id)).ExecuteDeleteAsync();
        }
        var run = new AgentRun { ResidentId = actor.UserId, Status = AgentRunStatus.Running, InputText = text, CreatedAt = DateTime.UtcNow };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        var id = run.Id;
        _ = Task.Run(() => ExecuteAsync(id, text, actor.UserId));
        return id;
    }

    async Task ExecuteAsync(int runId, string text, int residentId)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDb>();
        var delay = cfg.GetValue("Agent:StepDelayMs", 400);
        var seq = 0;
        var emitLock = new SemaphoreSlim(1, 1);
        async Task Emit(string type, object payload)
        {
            await emitLock.WaitAsync();
            try
            {
                db.AgentEvents.Add(new AgentEvent { RunId = runId, EventId = ++seq, Type = type, PayloadJson = JsonSerializer.Serialize(payload, Json), CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            finally { emitLock.Release(); }
            if (delay > 0) await Task.Delay(delay);
        }
        async Task<bool> Cancelled() => await db.AgentRuns.AsNoTracking().AnyAsync(r => r.Id == runId && r.Status == AgentRunStatus.Cancelled);
        async Task Finish(AgentRunStatus s, string? result = null) =>
            await db.AgentRuns.Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, s).SetProperty(r => r.ResultJson, result));
        try
        {
            await Emit("step", new { message = "주문 내용을 읽고 있어요." });
            if (!bool.TryParse(await new Settings(db).GetAsync(SettingKeys.AiEnabled), out var on) || !on)
            {
                await Emit("error", new { code = "AI_UNAVAILABLE", message = "AI를 쓸 수 없어요. 상품 검색으로 직접 담아 주세요." });
                await Finish(AgentRunStatus.Failed);
                return;
            }
            var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
            var items = await sp.GetRequiredService<IOrderInterpreter>().InterpretAsync(text, products, CancellationToken.None, Emit);
            if (await Cancelled()) return;
            var ok = items.Where(i => i.ProductId is not null).ToList();
            object? preview = null;
            if (ok.Count > 0)
            {
                try { preview = await sp.GetRequiredService<OrderService>().PreviewAsync(new PreviewInput(ok.Select(i => new ItemInput(i.ProductId!.Value, i.Qty, null, "AI")).ToList())); }
                catch (ApiException e) { await Emit("step", new { message = e.Message }); }
            }
            var draft = new { aiGenerated = true, notice = "AI가 만든 초안입니다. 확인한 뒤 직접 승인해 주세요.", items, preview };
            await Emit("draft", draft);
            await Emit("done", new { runId });
            await Finish(AgentRunStatus.Completed, JsonSerializer.Serialize(draft, Json));
        }
        catch (Exception e)
        {
            sp.GetRequiredService<ILogger<AgentService>>().LogError(e, "Agent run {RunId} failed", runId);
            try { await Emit("error", new { code = "AGENT_FAILED", message = "처리 중 오류가 났어요. 직접 담아 주세요." }); await Finish(AgentRunStatus.Failed); } catch { }
        }
    }

    public async Task<AgentRun> OwnedRunAsync(Actor actor, int id)
    {
        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id) ?? throw ApiException.NotFound("실행을 찾을 수 없습니다.");
        if (actor.Role == "resident" && run.ResidentId != actor.UserId) throw ApiException.NotFound("실행을 찾을 수 없습니다.");
        return run;
    }

    public async Task CancelAsync(Actor actor, int id)
    {
        await OwnedRunAsync(actor, id);
        await db.AgentRuns.Where(r => r.Id == id && r.Status == AgentRunStatus.Running).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Cancelled));
    }

    public async Task StreamAsync(HttpContext http, int id)
    {
        var actor = http.Actor();
        await OwnedRunAsync(actor, id);
        var last = int.TryParse(http.Request.Headers["Last-Event-ID"], out var l) ? l : 0;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var ct = http.RequestAborted;
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
        {
            var events = await db.AgentEvents.AsNoTracking().Where(e => e.RunId == id && e.EventId > last).OrderBy(e => e.EventId).ToListAsync(ct);
            foreach (var e in events)
            {
                await http.Response.WriteAsync($"id: {e.EventId}\nevent: {e.Type}\ndata: {e.PayloadJson}\n\n", ct);
                last = e.EventId;
            }
            await http.Response.Body.FlushAsync(ct);
            var status = await db.AgentRuns.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).FirstAsync(ct);
            if (status != AgentRunStatus.Running && events.Count == 0)
            {
                if (status == AgentRunStatus.Cancelled) await http.Response.WriteAsync("event: cancelled\ndata: {}\n\n", ct);
                return;
            }
            await Task.Delay(150, ct);
        }
    }
}
