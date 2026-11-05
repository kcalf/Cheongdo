using System.Text.Json.Serialization;
using Api.Data;
using Api.Endpoints;
using Api.Infra;
using Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var conn = builder.Configuration.GetConnectionString("Default") ?? "Data Source=chungdo.db";
builder.Services.AddDbContext<AppDb>(o =>
{
    if (conn.Contains("Server=", StringComparison.OrdinalIgnoreCase)) o.UseSqlServer(conn);
    else o.UseSqlite(conn);
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddScoped<Settings>();
builder.Services.AddScoped<IClock, Clock>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddSingleton<KeywordInterpreter>();
if (builder.Configuration.GetValue("Agent:Provider", "keyword") == "copilot")
    builder.Services.AddSingleton<IOrderInterpreter, CopilotInterpreter>();
else
    builder.Services.AddSingleton<IOrderInterpreter>(sp => sp.GetRequiredService<KeywordInterpreter>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Idempotent-Replay")));

var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
app.UseCors();
app.Use(async (c, next) => { c.Request.EnableBuffering(); await next(); });
Routes.Map(app);

using (var scope = app.Services.CreateScope())
    await Seeder.EnsureAsync(scope.ServiceProvider.GetRequiredService<AppDb>());

app.Run();

public partial class Program;
