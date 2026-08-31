using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using VNext.Memory.Domain;
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
builder.Services.AddScoped<HookIngestionService>();
builder.Services.AddSingleton<HookTokenValidator>();
builder.Services.AddHostedService<OutboxSyncWorker>();

builder.Services.AddHttpClient<CoreMemoryClient>(client =>
{
    client.BaseAddress = new Uri(edgeOptions.CoreUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("vnext-memory-edge/0.2.0");
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "vnext-memory-edge",
            Version = "0.2.0"
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
    version = "0.2.0",
    core = edgeOptions.CoreUrl,
    hookIngestion = string.IsNullOrWhiteSpace(edgeOptions.HookToken)
        ? "disabled"
        : "enabled"
}));

var api = app.MapGroup("/api/v1");

api.MapPost(
    "/memories/record",
    async (
        MemoryRecordRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.RecordAsync(request, cancellationToken)));

api.MapPost(
    "/memories/search",
    async (
        MemorySearchRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.SearchAsync(request, cancellationToken)));

api.MapPost(
    "/retrieval/search",
    async (
        MemorySearchRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.SearchWithTraceAsync(request, cancellationToken)));

api.MapPost(
    "/retrieval/feedback",
    async (
        RetrievalFeedbackRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.RecordFeedbackAsync(request, cancellationToken)));

api.MapPost(
    "/context/compile",
    async (
        ContextCompileRequest request,
        IEdgeMemoryGateway gateway,
        CancellationToken cancellationToken) =>
        Results.Ok(await gateway.CompileContextAsync(request, cancellationToken)));

api.MapPost(
    "/hooks/{provider}",
    async (
        string provider,
        JsonElement hookEvent,
        HttpContext httpContext,
        HookTokenValidator tokenValidator,
        HookIngestionService ingestionService,
        CancellationToken cancellationToken) =>
    {
        if (!tokenValidator.IsEnabled)
        {
            return Results.Json(
                new
                {
                    error = "hook_ingestion_disabled",
                    detail = "Configure Edge:HookToken before enabling assured hooks."
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!tokenValidator.Validate(
                httpContext.Request.Headers["X-VME-Hook-Token"].FirstOrDefault()))
        {
            return Results.Json(
                new
                {
                    error = "invalid_hook_token"
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var scope = new MemoryScope
        {
            ProjectId = Header(httpContext, "X-VME-Project"),
            RepositoryId = Header(httpContext, "X-VME-Repository"),
            Branch = Header(httpContext, "X-VME-Branch"),
            WorktreeId = Header(httpContext, "X-VME-Worktree"),
            Environment = Header(httpContext, "X-VME-Environment"),
            TaskId = Header(httpContext, "X-VME-Task")
        };

        return Results.Ok(await ingestionService.IngestAsync(
            provider,
            hookEvent,
            scope,
            cancellationToken));
    });

app.MapMcp("/mcp");

app.Run();

static string? Header(HttpContext context, string name)
{
    var value = context.Request.Headers[name].FirstOrDefault();
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class Program;
