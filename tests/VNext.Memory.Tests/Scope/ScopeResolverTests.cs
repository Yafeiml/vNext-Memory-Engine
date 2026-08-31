using Microsoft.VisualStudio.TestTools.UnitTesting;
using VNext.Memory.Application.Scope;
using VNext.Memory.Domain;

namespace VNext.Memory.Tests.Scope;

[TestClass]
public sealed class ScopeResolverTests
{
    private readonly ScopeResolver _resolver = new();

    [TestMethod]
    public void ProjectPreferenceOverridesGlobalPreference()
    {
        var global = Claim(
            "zh-CN",
            "interaction.response_language",
            new MemoryScope
            {
                TenantId = "personal",
                UserId = "anna"
            },
            DateTimeOffset.UtcNow.AddDays(-1));

        var project = Claim(
            "ja-JP",
            "interaction.response_language",
            new MemoryScope
            {
                TenantId = "personal",
                UserId = "anna",
                ProjectId = "vnext-memory"
            },
            DateTimeOffset.UtcNow);

        var result = _resolver.Resolve(
            [global, project],
            new MemoryScope
            {
                TenantId = "personal",
                UserId = "anna",
                ProjectId = "vnext-memory"
            },
            10);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("ja-JP", result[0].Statement);
    }

    [TestMethod]
    public void BranchScopedMemoryDoesNotLeakToAnotherBranch()
    {
        var claim = Claim(
            "仅设置 ShowInTaskbar 无效。",
            null,
            new MemoryScope
            {
                TenantId = "personal",
                ProjectId = "1remote",
                Branch = "fix/taskbar"
            },
            DateTimeOffset.UtcNow,
            MemoryKind.FailedApproach,
            ResolutionPolicy.SetUnion);

        var applicable = _resolver.IsApplicable(
            claim.Scope,
            new MemoryScope
            {
                TenantId = "personal",
                ProjectId = "1remote",
                Branch = "main"
            });

        Assert.IsFalse(applicable);
    }

    [TestMethod]
    public void AgentIdentityIsNotRequiredInMemoryScope()
    {
        var request = new MemoryRecordRequest
        {
            Content = "项目使用 pnpm。",
            Kind = MemoryKind.TechnicalFact,
            Trust = SourceTrust.CodeDerived,
            Scope = new MemoryScope
            {
                ProjectId = "vnext-memory"
            },
            AgentId = "codex"
        };

        var scope = _resolver.InferAndNormalizeScope(
            request,
            new RequestIdentity(
                "personal",
                "anna",
                Guid.NewGuid(),
                "codex",
                "session-1"));

        Assert.AreEqual("vnext-memory", scope.ProjectId);
        Assert.IsNull(scope.UserId);
    }

    private static MemoryClaim Claim(
        string statement,
        string? key,
        MemoryScope scope,
        DateTimeOffset updatedAt,
        MemoryKind kind = MemoryKind.Preference,
        ResolutionPolicy resolutionPolicy =
            ResolutionPolicy.MostSpecificOverride) =>
        new(
            Guid.NewGuid(),
            scope.TenantId ?? "personal",
            key,
            kind,
            resolutionPolicy,
            MemoryStatus.Active,
            statement,
            scope,
            0.9,
            1,
            1,
            updatedAt,
            updatedAt,
            null,
            0.8);
}
