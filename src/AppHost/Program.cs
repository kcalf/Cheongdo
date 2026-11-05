var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Api>("api")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Agent__Provider", builder.Configuration["Agent:Provider"] ?? "keyword");

if (builder.Configuration["Agent:GitHubToken"] is { Length: > 0 } token)
    api.WithEnvironment("Agent__GitHubToken", token);

builder.AddNpmApp("web", "../../web", "dev")
    .WithReference(api)
    .WaitFor(api)
    .WithHttpEndpoint(env: "PORT", port: 5173, isProxied: false)
    .WithExternalHttpEndpoints();

builder.Build().Run();
