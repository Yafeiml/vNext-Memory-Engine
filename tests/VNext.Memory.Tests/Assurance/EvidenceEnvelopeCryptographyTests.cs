using Microsoft.VisualStudio.TestTools.UnitTesting;
using VNext.Memory.Domain;

namespace VNext.Memory.Tests.Assurance;

[TestClass]
public sealed class EvidenceEnvelopeCryptographyTests
{
    private const string Secret = "test-device-token-with-enough-entropy";

    [TestMethod]
    public void SignedEvidenceRoundTripsAndDetectsTampering()
    {
        var envelope = EvidenceEnvelopeCryptography.SignEvidence(
            new MemoryRecordRequest
            {
                Content = "dotnet test passed.",
                Kind = MemoryKind.TechnicalFact,
                Trust = SourceTrust.ToolObserved,
                HasDeterministicEvidence = true
            },
            EvidenceChannel.TestResult,
            "test-adapter",
            Secret,
            new EvidenceProof(
                ToolName: "Bash",
                Command: "dotnet test",
                ExitCode: 0));

        Assert.IsTrue(EvidenceEnvelopeCryptography.VerifyEvidence(envelope, Secret));

        var tampered = envelope with
        {
            Observation = envelope.Observation with
            {
                Content = "dotnet test failed."
            }
        };
        Assert.IsFalse(EvidenceEnvelopeCryptography.VerifyEvidence(tampered, Secret));
    }

    [TestMethod]
    public void SignedFeedbackRoundTripsAndDetectsWrongSecret()
    {
        var envelope = EvidenceEnvelopeCryptography.SignFeedback(
            new RetrievalFeedbackRequest
            {
                TraceId = Guid.NewGuid(),
                Outcome = RetrievalOutcome.Confirmed,
                HasDeterministicEvidence = true
            },
            EvidenceChannel.TestResult,
            "test-adapter",
            Secret,
            new EvidenceProof(
                ToolName: "Bash",
                Command: "dotnet test",
                ExitCode: 0));

        Assert.IsTrue(EvidenceEnvelopeCryptography.VerifyFeedback(envelope, Secret));
        Assert.IsFalse(EvidenceEnvelopeCryptography.VerifyFeedback(
            envelope,
            "different-secret"));
    }
}
