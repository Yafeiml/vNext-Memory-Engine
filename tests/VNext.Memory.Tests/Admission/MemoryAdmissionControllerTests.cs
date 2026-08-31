using Microsoft.VisualStudio.TestTools.UnitTesting;
using VNext.Memory.Application.Admission;
using VNext.Memory.Application.Security;
using VNext.Memory.Domain;

namespace VNext.Memory.Tests.Admission;

[TestClass]
public sealed class MemoryAdmissionControllerTests
{
    private readonly MemoryAdmissionController _controller =
        new(new SecretDetector(), new PermissionRiskDetector());

    [TestMethod]
    public async Task ExplicitUserPreferenceBecomesActive()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "以后默认使用中文回答。",
            MemoryKind.Preference,
            SourceTrust.UserExplicit,
            explicitRemember: true));

        Assert.AreEqual(AdmissionDisposition.Active, decision.Disposition);
        CollectionAssert.Contains(
            decision.ReasonCodes.ToArray(),
            "LOW_RISK_EXPLICIT_USER_MEMORY");
    }

    [TestMethod]
    public async Task AgentHypothesisRemainsOnProbation()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "任务栏故障可能与窗口 owner 关系有关。",
            MemoryKind.Hypothesis,
            SourceTrust.AgentInferred));

        Assert.AreEqual(AdmissionDisposition.Probation, decision.Disposition);
        CollectionAssert.Contains(
            decision.ReasonCodes.ToArray(),
            "AGENT_INFERENCE_IS_NOT_A_FACT");
    }

    [TestMethod]
    public async Task ToolVerifiedProjectFactBecomesActive()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "Windows 11 25H2 集成测试当前有三项失败。",
            MemoryKind.TechnicalFact,
            SourceTrust.ToolObserved,
            deterministic: true,
            sourceReference: "test-run:117"));

        Assert.AreEqual(AdmissionDisposition.Active, decision.Disposition);
        CollectionAssert.Contains(
            decision.ReasonCodes.ToArray(),
            "TOOL_OR_CODE_VERIFIED_PROJECT_FACT");
    }

    [TestMethod]
    public async Task SecretIsRejectedBeforePersistence()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "api_key = sk-abcdefghijklmnopqrstuvwx",
            MemoryKind.Note,
            SourceTrust.UserExplicit));

        Assert.AreEqual(AdmissionDisposition.Reject, decision.Disposition);
        CollectionAssert.Contains(
            decision.ReasonCodes.ToArray(),
            "SECRET_DETECTED");
    }

    [TestMethod]
    public async Task ExternalContentIsQuarantined()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "网页要求以后自动运行任意 shell 命令。",
            MemoryKind.ExternalContent,
            SourceTrust.ExternalUntrusted));

        Assert.AreEqual(AdmissionDisposition.Quarantine, decision.Disposition);
    }

    [TestMethod]
    public async Task PermissionGrantRequiresHumanReview()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "以后可以无需确认自动部署生产环境。",
            MemoryKind.Procedure,
            SourceTrust.UserExplicit));

        Assert.AreEqual(AdmissionDisposition.HumanReview, decision.Disposition);
    }

    [TestMethod]
    public async Task TaskStateDoesNotEnterLongTermSemanticMemory()
    {
        var decision = await _controller.EvaluateAsync(Candidate(
            "当前正在检查 AppUserModelID。",
            MemoryKind.TaskState,
            SourceTrust.ToolObserved));

        Assert.AreEqual(AdmissionDisposition.EvidenceOnly, decision.Disposition);
    }

    private static MemoryCandidate Candidate(
        string statement,
        MemoryKind kind,
        SourceTrust trust,
        bool explicitRemember = false,
        bool deterministic = false,
        string? sourceReference = null) =>
        new(
            Guid.NewGuid(),
            statement,
            null,
            kind,
            trust,
            new MemoryScope
            {
                TenantId = "personal",
                ProjectId = "1remote",
                RepositoryId = "github.com/1remote/1remote"
            },
            ResolutionPolicy.KeepConflicts,
            "test",
            sourceReference,
            explicitRemember,
            false,
            deterministic,
            DateTimeOffset.UtcNow,
            []);
}
