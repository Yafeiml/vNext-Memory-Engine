using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using VNext.Memory.Edge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<EdgeOptions>(
    builder.Configuration.GetSection(EdgeOptions.SectionName));

var edgeOptions = builder.Configuration
    .GetSection(EdgeOptions.SectionName)
    .Get<EdgeOptions>() ?? new EdgeOptions();

if (string.IsNullOrWhiteSpace(edgeOptions.CoreToken))
{
    throw new InvalidOperationException(
        "Edge:CoreToken is required. Use one device token per edge, not one token per agent.");
}

builder.Services.AddSingleton<SqliteOutbox>();
builder.Services.AddScoped<IEdgeMemoryGateway, EdgeMemoryGateway>();
builder.Services.AddHostedService<OutboxSyncWorker>();

builder.Services.AddHttpClient<CoreMemoryClient>(client =>
{
    client.BaseAddress = new Uri(edgeOptions.CoreUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("vnext-memory-edge/0.1.0");
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "vnext-memory-edge",
            Version = "0.1.0"
        };
    })
    .WithHttpTransport()
    .WithTools<EdgeMcpTools>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var outbox = scope.ServiceProvider.GetRequiredService<SqliteOutbox>();
    await outbox.InitializeAsync();
}

app.Use(async (context, next) =>
{
    try
    {
        await next().ConfigureAwait(false);
    }
    catch (CoreMemoryRequestException exception)
    {
        context.Response.StatusCode = exception.Retryable
            ? StatusCodes.Status503ServiceUnavailable
            : (int)exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "memory_core_request_failed",
            retryable = exception.Retryable,
            detail = exception.Message
        }).ConfigureAwait(false);
    }
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "vnext-memory-edge",
    core = edgeOptions.CoreUrl
}));

var api = app.MapGroup("/api/v1");

api.MapPost(
    "/memories/record",
    async (
        VNext.Memory.Domain.MemoryRecordRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.RecordAsync(request, cancellationToken)));

api.MapPost(
    "/memories/search",
    async (
        VNext.Memory.Domain.MemorySearchRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.SearchAsync(request, cancellationToken)));

api.MapPost(
    "/context/compile",
    async (
        VNext.Memory.Domain.ContextCompileRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.CompileContextAsync(request, cancellationToken)));

app.MapMcp("/mcp");

app.Run();

public partial class Program;
