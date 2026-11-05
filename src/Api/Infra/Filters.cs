using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Data;
using Domain;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Infra;

public record Actor(string Role, int UserId)
{
    public string Key => $"{Role}:{UserId}";
}

public static class ActorExtensions
{
    public static Actor Actor(this HttpContext c) => (Actor)c.Items["actor"]!;
}

// 역할 헤더 검사. 시연용 구분이며 보안 경계가 아니다 (TRD 12절).
public class RoleFilter(string[] roles, bool operatorReadOnly = true) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var role = http.Request.Headers["X-Demo-Role"].ToString();
        if (role is not ("resident" or "store" or "courier" or "operator"))
            throw ApiException.Forbidden("X-Demo-Role 헤더가 필요합니다.");

        if (role == "operator")
        {
            var expected = http.RequestServices.GetRequiredService<IConfiguration>()["OperatorKey"];
            var given = http.Request.Headers["X-Operator-Key"].ToString();
            var ok = !string.IsNullOrEmpty(expected) && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));
            if (!ok)
            {
                var db = http.RequestServices.GetRequiredService<AppDb>();
                Audit.Add(db, "operator", "OperatorKeyRejected", "Endpoint", http.Request.Path, null, http.TraceIdentifier);
                await db.SaveChangesAsync();
                throw ApiException.Forbidden("운영자 키가 올바르지 않습니다.");
            }
            var allowed = roles.Contains("operator") || (operatorReadOnly && HttpMethods.IsGet(http.Request.Method));
            if (!allowed) throw ApiException.Forbidden("이 경로는 운영자가 접근할 수 없습니다.");
            http.Items["actor"] = new Actor(role, 0);
            return await next(ctx);
        }

        if (!roles.Contains(role)) throw ApiException.Forbidden($"{role} 역할은 이 경로에 접근할 수 없습니다.");
        if (!int.TryParse(http.Request.Headers["X-Demo-User-Id"], out var uid))
            throw ApiException.BadRequest("INVALID_USER", "X-Demo-User-Id 헤더가 필요합니다.");
        http.Items["actor"] = new Actor(role, uid);
        return await next(ctx);
    }
}

// Idempotency-Key 헤더가 있으면 같은 요청을 한 번만 처리한다 (TRD 11.3).
public class IdempotencyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key) || !HttpMethods.IsPost(http.Request.Method) && !HttpMethods.IsPut(http.Request.Method))
            return await next(ctx);

        var db = http.RequestServices.GetRequiredService<AppDb>();
        var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var actor = http.Actor().Key;
        var endpoint = $"{http.Request.Method} {http.Request.Path}";

        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        http.Request.Body.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}{http.Request.QueryString}\n{body}")));

        var rec = new IdempotencyRecord { Key = key, Endpoint = endpoint, ActorId = actor, RequestHash = hash, CreatedAt = DateTime.UtcNow };
        db.IdempotencyRecords.Add(rec);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var old = await db.IdempotencyRecords.AsNoTracking().FirstAsync(r => r.Key == key && r.Endpoint == endpoint && r.ActorId == actor);
            if (old.RequestHash != hash) throw ApiException.Conflict("IDEMPOTENCY_MISMATCH", "같은 키로 다른 요청을 보냈습니다.");
            if (!old.Done) throw ApiException.Conflict("IDEMPOTENCY_IN_PROGRESS", "처리 중입니다. 잠시 뒤 다시 시도하세요.");
            http.Response.Headers["Idempotent-Replay"] = "true";
            return old.ResponseJson is null
                ? Results.StatusCode(old.ResponseStatus ?? 200)
                : Results.Content(old.ResponseJson, "application/json", Encoding.UTF8, old.ResponseStatus);
        }

        object? result;
        try { result = await next(ctx); }
        catch
        {
            db.ChangeTracker.Clear();
            await db.IdempotencyRecords.Where(r => r.Key == key && r.Endpoint == endpoint && r.ActorId == actor).ExecuteDeleteAsync();
            throw;
        }

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var value = (result as IValueHttpResult)?.Value;
        var payload = value is null ? null : JsonSerializer.Serialize(value, json);
        await db.IdempotencyRecords.Where(r => r.Key == key && r.Endpoint == endpoint && r.ActorId == actor)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Done, true)
                .SetProperty(r => r.ResponseStatus, status)
                .SetProperty(r => r.ResponseJson, payload));
        return result;
    }
}

public class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (ApiException e) { await Write(ctx, e.Status, e.Code, e.Message); }
        catch (BadHttpRequestException) { await Write(ctx, 400, "BAD_REQUEST", "요청 형식이 올바르지 않습니다."); }
        catch (DbUpdateConcurrencyException) { await Write(ctx, 409, "CONFLICT", "동시에 변경되었습니다. 다시 시도하세요."); }
        catch (Exception e)
        {
            log.LogError(e, "Unhandled error {TraceId}", ctx.TraceIdentifier);
            await Write(ctx, 500, "INTERNAL_ERROR", "서버 오류가 발생했습니다.");
        }
    }

    static Task Write(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted) return Task.CompletedTask;
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { code, message, traceId = ctx.TraceIdentifier });
    }
}
