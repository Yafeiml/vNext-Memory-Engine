using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using VNext.Memory.Application;
using VNext.Memory.Core;
using VNext.Memory.Domain;
using VNext.Memory.Infrastructure.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<CoreSecurityOptions>(
    builder.Configuration.GetSection(CoreSecurityOptions.SectionName));

var connectionString =
    builder.Configuration.GetConnectionString("Memory") ??
    builder.Configuration["Postgres:ConnectionString"] ??
    throw new InvalidOperationException(
        "A PostgreSQL connection string is required in ConnectionStrings:Memory.");

builder.Services
    .AddMemoryEngine()
    .AddPostgresMemory(connectionString);

builder.Services.AddScoped<RequestIdentityAccessor>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "vnext-memory-core",
            Version = "0.1.0"
        };
    })
    .WithHttpTransport()
    .WithTools<MemoryMcpTools>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var migrator = scope.ServiceProvider
        .GetRequiredService<PostgresSchemaMigrator>();
    await migrator.MigrateAsync();
}

app.UseMiddleware<DeviceAuthenticationMiddleware>();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "vnext-memory-core",
    version = "0.1.0"
}));

app.MapGet("/ready", () => Results.Ok(new
{
    status = "ready"
}));

var api = app.MapGroup("/api/v1");

api.MapPost(
    "/devices/register",
    async (
        DeviceRegistrationRequest request,
        RequestIdentityAccessor identityAccessor,
        IDeviceIdentityStore identityStore,
        CancellationToken cancellationToken) =>
    {
        var identity = identityAccessor.GetRequiredIdentity();
        if (!identity.IsAdministrator)
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.TenantId) ||
            string.IsNullOrWhiteSpace(request.PrincipalId) ||
            string.IsNullOrWhiteSpace(request.DeviceName))
        {
            return Results.BadRequest(new
            {
                error = "tenantId, principalId, and deviceName are required."
            });
        }

        var (rawToken, tokenHash) = DeviceTokenFactory.Create();
        var result = await identityStore.RegisterAsync(
            request,
            rawToken,
            tokenHash,
            cancellationToken);

        return Results.Created(
            $"/api/v1/devices/{result.DeviceId}",
            result);
    });

api.MapPost(
    "/memories/record",
    async (
        MemoryRecordRequest request,
        RequestIdentityAccessor identityAccessor,
        IMemoryService memoryService,
        CancellationToken cancellationToken) =>
    {
        var result = await memoryService.RecordAsync(
            request,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);
        return Results.Ok(result);
    });

api.MapPost(
    "/memories/search",
    async (
        MemorySearchRequest request,
        RequestIdentityAccessor identityAccessor,
        IMemoryService memoryService,
        CancellationToken cancellationToken) =>
    {
        var result = await memoryService.SearchAsync(
            request,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);
        return Results.Ok(result);
    });

api.MapPost(
    "/context/compile",
    async (
        ContextCompileRequest request,
        RequestIdentityAccessor identityAccessor,
        IMemoryService memoryService,
        CancellationToken cancellationToken) =>
    {
        var result = await memoryService.CompileContextAsync(
            request,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);
        return Results.Ok(result);
    });

api.MapGet(
    "/memories/{claimId:guid}/explain",
    async (
        Guid claimId,
        RequestIdentityAccessor identityAccessor,
        IMemoryService memoryService,
        CancellationToken cancellationToken) =>
    {
        var result = await memoryService.ExplainAsync(
            claimId,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    });

app.MapMcp("/mcp");

app.Run();

public partial class Program;
