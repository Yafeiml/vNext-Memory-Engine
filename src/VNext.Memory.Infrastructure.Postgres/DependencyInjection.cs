using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VNext.Memory.Domain;

namespace VNext.Memory.Infrastructure.Postgres;

public static class DependencyInjection
{
    public static IServiceCollection AddPostgresMemory(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton(_ =>
            new NpgsqlDataSourceBuilder(connectionString).Build());

        services.AddSingleton<PostgresSchemaMigrator>();
        services.AddScoped<PostgresMemoryRepository>();
        services.AddScoped<PostgresAssuredMemoryRepository>();
        services.AddScoped<PostgresRetrievalTelemetryStore>();
        services.AddScoped<IMemoryRepository>(
            provider => provider.GetRequiredService<PostgresAssuredMemoryRepository>());
        services.AddScoped<IDeviceIdentityStore>(
            provider => provider.GetRequiredService<PostgresMemoryRepository>());
        services.AddScoped<IRetrievalTelemetryStore>(
            provider => provider.GetRequiredService<PostgresRetrievalTelemetryStore>());

        return services;
    }
}
