using Microsoft.Extensions.DependencyInjection;
using VNext.Memory.Application.Admission;
using VNext.Memory.Application.Assurance;
using VNext.Memory.Application.Scope;
using VNext.Memory.Application.Security;
using VNext.Memory.Application.Services;
using VNext.Memory.Domain;

namespace VNext.Memory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddMemoryEngine(
        this IServiceCollection services)
    {
        services.AddSingleton<ISecretDetector, SecretDetector>();
        services.AddSingleton<IPermissionRiskDetector, PermissionRiskDetector>();
        services.AddSingleton<IMemoryAdmissionController, MemoryAdmissionController>();
        services.AddSingleton<ISourceAssuranceEvaluator, SourceAssuranceEvaluator>();
        services.AddSingleton<IScopeResolver, ScopeResolver>();
        services.AddSingleton<IRetrievalTelemetryStore>(
            NoopRetrievalTelemetryStore.Instance);
        services.AddScoped<IMemoryService, MemoryService>();
        return services;
    }
}
