using System.Text.Json;
using Api.Data;
using Domain;
using Microsoft.EntityFrameworkCore;

namespace Api.Infra;

public class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static ApiException BadRequest(string code, string message) => new(400, code, message);
    public static ApiException Forbidden(string message) => new(403, "FORBIDDEN", message);
    public static ApiException NotFound(string message) => new(404, "NOT_FOUND", message);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
}

public interface IClock
{
    Task<DateTime> NowKstAsync();
}

public static class Time
{
    public static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);
    public static DateTime ToKst(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(KstOffset);
    public static DateTime KstToUtc(DateTime kst) => DateTime.SpecifyKind(kst - KstOffset, DateTimeKind.Utc);
    public static string Iso(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(KstOffset).ToString("yyyy-MM-ddTHH:mm:sszzz");
}

public class Settings(AppDb db)
{
    public async Task<string?> GetAsync(string key) =>
        (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;

    public async Task<int> GetIntAsync(string key, int fallback) =>
        int.TryParse(await GetAsync(key), out var v) ? v : fallback;

    public async Task SetAsync(string key, string? value)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (value is null) { if (row is not null) db.AppSettings.Remove(row); }
        else if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }
}

public class Clock(Settings settings) : IClock
{
    public async Task<DateTime> NowKstAsync()
    {
        var demo = await settings.GetAsync(SettingKeys.DemoClock);
        if (demo is not null && DateTime.TryParse(demo, out var fixedKst)) return fixedKst;
        return Time.ToKst(DateTime.UtcNow);
    }
}

public static class Audit
{
    public static void Add(AppDb db, string role, string action, string targetType, object targetId, object? detail, string traceId) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorRole = role, Action = action, TargetType = targetType, TargetId = targetId.ToString() ?? "",
            DetailJson = detail is null ? "{}" : JsonSerializer.Serialize(detail), TraceId = traceId, CreatedAt = DateTime.UtcNow,
        });
}
