using Microsoft.VisualStudio.TestTools.UnitTesting;
using VNext.Memory.Application.Assurance;
using VNext.Memory.Domain;

namespace VNext.Memory.Tests.Assurance;

[TestClass]
public sealed class SourceAssuranceEvaluatorTests
{
    private readonly SourceAssuranceEvaluator _evaluator = new();
    private readonly RequestIdentity _identity = new(
        "personal",
        "anna",
        Guid.NewGuid(),
        "codex",
        "session-1");

    [TestMethod]
    public void UnsignedClientCannotClaimToolTrust()
    {
        var request = Request() with
        {
            Trust = SourceTrust.ToolObserved,
            HasDeterministicEvidence = true
        };

        var assurance = _evaluator.Evaluate(
            request,
            _identity,
            new SourceAssertionContext(
                EvidenceChannel.LegacyClient,
                AssuranceLevel.Authenticated,
                request.Trust,
                SignatureVerified: false,
                "legacy-rest"));

        Assert.AreEqual(SourceTrust.AgentInferred, assurance.EffectiveTrust);
        Assert.IsFalse(assurance.DeterministicEvidenceVerified);
        CollectionAssert.Contains(
            assurance.ReasonCodes.ToArray(),
            "UNSIGNED_SOURCE_CLAIMS_IGNORED");
    }

    [TestMethod]
    public void SignedUserCorrectionReceivesUserCorrectionTrust()
    {
        var request = Request() with
        {
            Trust = SourceTrust.AgentInferred,
            IsCorrection = true,
            ExplicitRemember = true
        };

        var assurance = _evaluator.Evaluate(
            request,
            _identity,
            SignedContext(EvidenceChannel.UserMessage));

        Assert.AreEqual(SourceTrust.UserCorrection, assurance.EffectiveTrust);
        Assert.IsTrue(assurance.CorrectionVerified);
        Assert.IsTrue(assurance.ExplicitRememberVerified);
    }

    [TestMethod]
    public void SignedTestResultWithExitCodeIsDeterministic()
    {
        var request = Request() with
        {
            Trust = SourceTrust.AgentInferred,
            HasDeterministicEvidence = true
        };

        var assurance = _evaluator.Evaluate(
            request,
            _identity,
            SignedContext(
                EvidenceChannel.TestResult,
                new EvidenceProof(
                    ProviderEventType: "PostToolUse",
                    ToolName: "Bash",
                    Command: "dotnet test",
                    ExitCode: 0)));

        Assert.AreEqual(SourceTrust.ToolObserved, assurance.EffectiveTrust);
        Assert.AreEqual(AssuranceLevel.Deterministic, assurance.Level);
        Assert.IsTrue(assurance.DeterministicEvidenceVerified);
    }

    [TestMethod]
    public void SignedAgentObservationCannotSelfPromote()
    {
        var request = Request() with
        {
            Trust = SourceTrust.HumanApproved,
            ExplicitRemember = true,
            HasDeterministicEvidence = true
        };

        var assurance = _evaluator.Evaluate(
            request,
            _identity,
            SignedContext(EvidenceChannel.AgentObservation));

        Assert.AreEqual(SourceTrust.AgentInferred, assurance.EffectiveTrust);
        Assert.IsFalse(assurance.ExplicitRememberVerified);
        Assert.IsFalse(assurance.DeterministicEvidenceVerified);
    }

    private static MemoryRecordRequest Request() =>
        new()
        {
            Content = "The integration test completed successfully.",
            Kind = MemoryKind.TechnicalFact,
            Scope = new MemoryScope
            {
                ProjectId = "vnext-memory",
                RepositoryId = "github.com/yafeiml/vnext-memory-engine"
            }
        };

    private static SourceAssertionContext SignedContext(
        EvidenceChannel channel,
        EvidenceProof? proof = null) =>
        new(
            channel,
            AssuranceLevel.Signed,
            SourceTrust.AgentInferred,
            SignatureVerified: true,
            "test-adapter",
            Proof: proof,
            EventId: Guid.NewGuid(),
            PayloadHash: new string('a', 64));
}
