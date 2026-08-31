using Microsoft.VisualStudio.TestTools.UnitTesting;
using VNext.Memory.Application.Scope;
using VNext.Memory.Application.Services;
using VNext.Memory.Domain;

namespace VNext.Memory.Tests.Scope;

[TestClass]
public sealed class MemoryServiceScopeTests
{
    [TestMethod]
    public async Task SearchAutomaticallyIncludesCurrentPrincipalPreferences()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new SearchOnlyRepository(
        [
            new MemoryClaim(
                Guid.NewGuid(),
                "personal",
                "interaction.response_language",
                MemoryKind.Preference,
                ResolutionPolicy.MostSpecificOverride,
                MemoryStatus.Active,
                "zh-CN",
                new MemoryScope
                {
                    TenantId = "personal",
                    UserId = "anna"
                },
                0.95,
                1,
                1,
                now,
                now,
                null,
                0.8),
            new MemoryClaim(
                Guid.NewGuid(),
                "personal",
                null,
                MemoryKind.TechnicalFact,
                ResolutionPolicy.AppendOnly,
                MemoryStatus.Active,
                "Project uses .NET 10.",
                new MemoryScope
                {
                    TenantId = "personal",
                    ProjectId = "vnext-memory"
                },
                0.95,
                1,
                1,
                now,
                now,
                null,
                0.8)
        ]);

        var service = new MemoryService(
            repository,
            new NeverUsedAdmissionController(),
            new ScopeResolver());

        var results = await service.SearchAsync(
            new MemorySearchRequest
            {
                Query = "memory",
                Scope = new MemoryScope
                {
                    ProjectId = "vnext-memory"
                },
                Limit = 10
            },
            new RequestIdentity(
                "personal",
                "anna",
                Guid.NewGuid(),
                "codex",
                "session-1"));

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(result => result.Statement == "zh-CN"));
        Assert.IsTrue(results.Any(result => result.Statement == "Project uses .NET 10."));
    }

    private sealed class SearchOnlyRepository(
        IReadOnlyList<MemoryClaim> claims) : IMemoryRepository
    {
        public Task<IReadOnlyList<MemoryClaim>> SearchAsync(
            string tenantId,
            string query,
            bool includeProbation,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(claims);

        public Task<PersistMemoryResult> PersistAsync(
            EvidenceEvent evidence,
            MemoryCandidate candidate,
            AdmissionDecision decision,
            string dedupeKey,
            string scopeHash,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryExplanation?> ExplainAsync(
            string tenantId,
            Guid claimId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NeverUsedAdmissionController : IMemoryAdmissionController
    {
        public ValueTask<AdmissionDecision> EvaluateAsync(
            MemoryCandidate candidate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
