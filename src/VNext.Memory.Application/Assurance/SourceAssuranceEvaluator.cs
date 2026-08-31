using VNext.Memory.Domain;

namespace VNext.Memory.Application.Assurance;

public sealed class SourceAssuranceEvaluator : ISourceAssuranceEvaluator
{
    public SourceAssurance Evaluate(
        MemoryRecordRequest request,
        RequestIdentity identity,
        SourceAssertionContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(context);

        var reasons = NewReasons(context);
        var effectiveTrust = SourceTrust.AgentInferred;
        var level = context.Level;
        var explicitRememberVerified = false;
        var correctionVerified = false;
        var deterministicVerified = false;

        if (identity.IsAdministrator &&
            context.Channel is EvidenceChannel.Administrative or EvidenceChannel.LegacyClient)
        {
            effectiveTrust = request.Trust == SourceTrust.PolicyManaged
                ? SourceTrust.PolicyManaged
                : SourceTrust.HumanApproved;
            level = effectiveTrust == SourceTrust.PolicyManaged
                ? AssuranceLevel.PolicyManaged
                : AssuranceLevel.HumanAttested;
            explicitRememberVerified = request.ExplicitRemember;
            correctionVerified = request.IsCorrection;
            deterministicVerified = request.HasDeterministicEvidence &&
                                    HasDeterministicProof(context.Channel, context.Proof);
            reasons.Add("ADMINISTRATIVE_SOURCE_ATTESTED");
        }
        else if (!context.SignatureVerified)
        {
            effectiveTrust = context.Channel == EvidenceChannel.ExternalContent ||
                             request.Kind == MemoryKind.ExternalContent
                ? SourceTrust.ExternalUntrusted
                : SourceTrust.AgentInferred;
            level = context.Level == AssuranceLevel.Unverified
                ? AssuranceLevel.Unverified
                : AssuranceLevel.Authenticated;
            reasons.Add("UNSIGNED_SOURCE_CLAIMS_IGNORED");
        }
        else
        {
            switch (context.Channel)
            {
                case EvidenceChannel.UserMessage:
                    effectiveTrust = request.IsCorrection
                        ? SourceTrust.UserCorrection
                        : SourceTrust.UserExplicit;
                    explicitRememberVerified = request.ExplicitRemember;
                    correctionVerified = request.IsCorrection;
                    reasons.Add("SIGNED_USER_MESSAGE");
                    break;

                case EvidenceChannel.ToolResult:
                case EvidenceChannel.TestResult:
                    effectiveTrust = SourceTrust.ToolObserved;
                    deterministicVerified = request.HasDeterministicEvidence &&
                                            HasDeterministicProof(
                                                context.Channel,
                                                context.Proof);
                    reasons.Add("SIGNED_TOOL_EVENT");
                    break;

                case EvidenceChannel.CodeArtifact:
                case EvidenceChannel.GitResult:
                    effectiveTrust = SourceTrust.CodeDerived;
                    deterministicVerified = request.HasDeterministicEvidence &&
                                            HasDeterministicProof(
                                                context.Channel,
                                                context.Proof);
                    reasons.Add("SIGNED_CODE_OR_GIT_EVENT");
                    break;

                case EvidenceChannel.ExternalContent:
                    effectiveTrust = SourceTrust.ExternalUntrusted;
                    reasons.Add("SIGNED_EXTERNAL_CONTENT_CAPTURE");
                    break;

                case EvidenceChannel.Administrative:
                    effectiveTrust = SourceTrust.AgentInferred;
                    reasons.Add("NON_ADMINISTRATIVE_CALLER_DOWNGRADED");
                    break;

                default:
                    effectiveTrust = SourceTrust.AgentInferred;
                    reasons.Add("SIGNED_AGENT_OBSERVATION");
                    break;
            }

            level = deterministicVerified
                ? AssuranceLevel.Deterministic
                : AssuranceLevel.Signed;
        }

        if (request.Trust != effectiveTrust)
        {
            reasons.Add("CLIENT_TRUST_CLAIM_REPLACED");
        }

        if (request.ExplicitRemember && !explicitRememberVerified)
        {
            reasons.Add("EXPLICIT_REMEMBER_CLAIM_NOT_VERIFIED");
        }

        if (request.IsCorrection && !correctionVerified)
        {
            reasons.Add("CORRECTION_CLAIM_NOT_VERIFIED");
        }

        if (request.HasDeterministicEvidence && !deterministicVerified)
        {
            reasons.Add("DETERMINISTIC_EVIDENCE_CLAIM_NOT_VERIFIED");
        }

        return Build(
            context,
            level,
            request.Trust,
            effectiveTrust,
            explicitRememberVerified,
            correctionVerified,
            deterministicVerified,
            reasons);
    }

    public SourceAssurance EvaluateFeedback(
        RetrievalFeedbackRequest request,
        RequestIdentity identity,
        SourceAssertionContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(context);

        var reasons = NewReasons(context);
        var effectiveTrust = SourceTrust.AgentInferred;
        var level = context.Level;
        var deterministicVerified = false;

        if (identity.IsAdministrator)
        {
            effectiveTrust = SourceTrust.HumanApproved;
            level = AssuranceLevel.HumanAttested;
            deterministicVerified = request.HasDeterministicEvidence &&
                                    HasDeterministicProof(context.Channel, context.Proof);
            reasons.Add("ADMINISTRATIVE_FEEDBACK");
        }
        else if (!context.SignatureVerified)
        {
            level = AssuranceLevel.Authenticated;
            reasons.Add("UNSIGNED_FEEDBACK_IGNORED");
        }
        else
        {
            switch (context.Channel)
            {
                case EvidenceChannel.UserMessage:
                    effectiveTrust = request.Outcome is RetrievalOutcome.Confirmed or
                        RetrievalOutcome.Contradicted or RetrievalOutcome.Harmful
                        ? SourceTrust.UserCorrection
                        : SourceTrust.UserExplicit;
                    reasons.Add("SIGNED_USER_FEEDBACK");
                    break;

                case EvidenceChannel.ToolResult:
                case EvidenceChannel.TestResult:
                    effectiveTrust = SourceTrust.ToolObserved;
                    deterministicVerified = request.HasDeterministicEvidence &&
                                            HasDeterministicProof(
                                                context.Channel,
                                                context.Proof);
                    reasons.Add("SIGNED_TOOL_FEEDBACK");
                    break;

                case EvidenceChannel.CodeArtifact:
                case EvidenceChannel.GitResult:
                    effectiveTrust = SourceTrust.CodeDerived;
                    deterministicVerified = request.HasDeterministicEvidence &&
                                            HasDeterministicProof(
                                                context.Channel,
                                                context.Proof);
                    reasons.Add("SIGNED_CODE_FEEDBACK");
                    break;

                default:
                    effectiveTrust = SourceTrust.AgentInferred;
                    reasons.Add("AGENT_FEEDBACK_NON_AUTHORITATIVE");
                    break;
            }

            level = deterministicVerified
                ? AssuranceLevel.Deterministic
                : AssuranceLevel.Signed;
        }

        return Build(
            context,
            level,
            context.ClaimedTrust,
            effectiveTrust,
            false,
            false,
            deterministicVerified,
            reasons);
    }

    private static bool HasDeterministicProof(
        EvidenceChannel channel,
        EvidenceProof? proof)
    {
        if (proof is null)
        {
            return false;
        }

        return channel switch
        {
            EvidenceChannel.ToolResult or EvidenceChannel.TestResult =>
                proof.ExitCode.HasValue &&
                (!string.IsNullOrWhiteSpace(proof.ToolName) ||
                 !string.IsNullOrWhiteSpace(proof.Command)),
            EvidenceChannel.GitResult =>
                proof.ExitCode.HasValue &&
                (!string.IsNullOrWhiteSpace(proof.CommitSha) ||
                 !string.IsNullOrWhiteSpace(proof.Command)),
            EvidenceChannel.CodeArtifact =>
                !string.IsNullOrWhiteSpace(proof.ArtifactDigest) ||
                !string.IsNullOrWhiteSpace(proof.CommitSha),
            _ => false
        };
    }

    private static List<string> NewReasons(SourceAssertionContext context) =>
        context.ReasonCodes?
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    private static SourceAssurance Build(
        SourceAssertionContext context,
        AssuranceLevel level,
        SourceTrust claimedTrust,
        SourceTrust effectiveTrust,
        bool explicitRememberVerified,
        bool correctionVerified,
        bool deterministicVerified,
        IEnumerable<string> reasons) =>
        new(
            context.Channel,
            level,
            claimedTrust,
            effectiveTrust,
            context.SignatureVerified,
            explicitRememberVerified,
            correctionVerified,
            deterministicVerified,
            context.Adapter,
            context.AdapterVersion,
            context.Proof,
            context.EventId,
            context.PayloadHash,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
}
