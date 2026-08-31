using VNext.Memory.Application.Security;
using VNext.Memory.Domain;

namespace VNext.Memory.Application.Admission;

public sealed class MemoryAdmissionController(
    ISecretDetector secretDetector,
    IPermissionRiskDetector permissionRiskDetector) : IMemoryAdmissionController
{
    private const int MaxLongTermStatementLength = 4_000;

    public ValueTask<AdmissionDecision> EvaluateAsync(
        MemoryCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(candidate.Statement) ||
            candidate.Statement.Trim().Length < 4)
        {
            return ValueTask.FromResult(Decision(
                AdmissionDisposition.Reject,
                0,
                "CONTENT_TOO_SHORT"));
        }

        var secretResult = secretDetector.Inspect(candidate.Statement);
        if (secretResult.ContainsSecret)
        {
            return ValueTask.FromResult(new AdmissionDecision(
                AdmissionDisposition.Reject,
                0,
                ["SECRET_DETECTED", .. secretResult.ReasonCodes],
                ["DO_NOT_PERSIST_RAW_CONTENT"]));
        }

        if (candidate.Trust == SourceTrust.ExternalUntrusted ||
            candidate.Kind == MemoryKind.ExternalContent)
        {
            return ValueTask.FromResult(Decision(
                AdmissionDisposition.Quarantine,
                10,
                "EXTERNAL_UNTRUSTED_SOURCE"));
        }

        if (permissionRiskDetector.IsHighRisk(candidate))
        {
            return ValueTask.FromResult(new AdmissionDecision(
                AdmissionDisposition.HumanReview,
                45,
                ["HIGH_IMPACT_PERMISSION_OR_ACTION"],
                ["REQUIRE_EXPLICIT_HUMAN_CONFIRMATION"]));
        }

        if (candidate.Kind == MemoryKind.TaskState)
        {
            return ValueTask.FromResult(Decision(
                AdmissionDisposition.EvidenceOnly,
                35,
                "TASK_STATE_BELONGS_TO_OPERATIONAL_STORE"));
        }

        if (candidate.Statement.Length > MaxLongTermStatementLength)
        {
            return ValueTask.FromResult(Decision(
                AdmissionDisposition.EvidenceOnly,
                30,
                "CONTENT_TOO_LARGE_FOR_SEMANTIC_MEMORY"));
        }

        var score = CalculateScore(candidate);
        var reasons = new List<string>();

        if (candidate.ExplicitRemember)
        {
            reasons.Add("EXPLICIT_REMEMBER_REQUEST");
        }

        if (candidate.IsCorrection)
        {
            reasons.Add("USER_CORRECTION");
        }

        if (candidate.HasDeterministicEvidence)
        {
            reasons.Add("DETERMINISTIC_EVIDENCE");
        }

        if (candidate.Scope.IsProjectScoped)
        {
            reasons.Add("PROJECT_SCOPE_CLEAR");
        }

        var disposition = ResolveDisposition(candidate, score, reasons);

        return ValueTask.FromResult(new AdmissionDecision(
            disposition,
            score,
            reasons,
            RequiredActions(disposition),
            disposition == AdmissionDisposition.Probation
                ? DateTimeOffset.UtcNow.AddDays(7)
                : null));
    }

    private static AdmissionDisposition ResolveDisposition(
        MemoryCandidate candidate,
        double score,
        ICollection<string> reasons)
    {
        if (candidate.Trust == SourceTrust.PolicyManaged)
        {
            reasons.Add("POLICY_MANAGED");
            return AdmissionDisposition.Active;
        }

        if (candidate.ExplicitRemember &&
            candidate.Trust >= SourceTrust.UserExplicit &&
            (candidate.Kind is MemoryKind.Preference or MemoryKind.Constraint or MemoryKind.Decision))
        {
            reasons.Add("LOW_RISK_EXPLICIT_USER_MEMORY");
            return AdmissionDisposition.Active;
        }

        if (candidate.IsCorrection &&
            candidate.Trust >= SourceTrust.UserCorrection &&
            (candidate.Kind is MemoryKind.Preference or MemoryKind.Constraint or MemoryKind.FailedApproach))
        {
            reasons.Add("CORRECTION_HAS_HIGH_MEMORY_VALUE");
            return AdmissionDisposition.Active;
        }

        if (candidate.Kind == MemoryKind.Preference &&
            candidate.Trust >= SourceTrust.UserExplicit)
        {
            reasons.Add("USER_OWNED_PREFERENCE");
            return AdmissionDisposition.Active;
        }

        if ((candidate.Kind is MemoryKind.TechnicalFact or MemoryKind.FailedApproach) &&
            (candidate.Trust is SourceTrust.ToolObserved or SourceTrust.CodeDerived) &&
            candidate.HasDeterministicEvidence &&
            candidate.Scope.IsProjectScoped &&
            !string.IsNullOrWhiteSpace(candidate.SourceReference))
        {
            reasons.Add("TOOL_OR_CODE_VERIFIED_PROJECT_FACT");
            return AdmissionDisposition.Active;
        }

        if (candidate.Kind == MemoryKind.Decision &&
            candidate.Trust >= SourceTrust.UserExplicit &&
            candidate.Scope.IsProjectScoped)
        {
            reasons.Add("USER_CONFIRMED_PROJECT_DECISION");
            return AdmissionDisposition.Active;
        }

        if (candidate.Kind == MemoryKind.Procedure)
        {
            if (candidate.Trust >= SourceTrust.HumanApproved)
            {
                reasons.Add("HUMAN_APPROVED_PROCEDURE");
                return AdmissionDisposition.Active;
            }

            reasons.Add("PROCEDURE_REQUIRES_REPEATED_SUCCESS");
            return AdmissionDisposition.Probation;
        }

        if (candidate.Kind == MemoryKind.Hypothesis ||
            candidate.Trust == SourceTrust.AgentInferred)
        {
            reasons.Add("AGENT_INFERENCE_IS_NOT_A_FACT");
            return AdmissionDisposition.Probation;
        }

        if (candidate.Kind == MemoryKind.Note)
        {
            reasons.Add("UNSTRUCTURED_NOTE");
            return AdmissionDisposition.EvidenceOnly;
        }

        if (score >= 75)
        {
            reasons.Add("HIGH_ADMISSION_SCORE");
            return AdmissionDisposition.Active;
        }

        if (score >= 52)
        {
            reasons.Add("PROBATION_SCORE_RANGE");
            return AdmissionDisposition.Probation;
        }

        reasons.Add("INSUFFICIENT_LONG_TERM_VALUE");
        return AdmissionDisposition.EvidenceOnly;
    }

    private static double CalculateScore(MemoryCandidate candidate)
    {
        var utility = candidate.Kind switch
        {
            MemoryKind.FailedApproach => 19,
            MemoryKind.Decision => 18,
            MemoryKind.Constraint => 17,
            MemoryKind.Preference => 15,
            MemoryKind.TechnicalFact => 15,
            MemoryKind.Procedure => 14,
            MemoryKind.Hypothesis => 8,
            _ => 5
        };

        var evidence = (int)candidate.Trust / 5.0;
        var persistence = candidate.Kind switch
        {
            MemoryKind.Preference => 14,
            MemoryKind.Decision => 14,
            MemoryKind.Constraint => 14,
            MemoryKind.Procedure => 12,
            MemoryKind.FailedApproach => 11,
            MemoryKind.TechnicalFact => 9,
            MemoryKind.Hypothesis => 4,
            _ => 3
        };

        var outcomeImpact = candidate.Kind switch
        {
            MemoryKind.FailedApproach => 15,
            MemoryKind.Decision => 14,
            MemoryKind.Constraint => 13,
            MemoryKind.Procedure => 12,
            MemoryKind.TechnicalFact => 10,
            MemoryKind.Preference => 8,
            _ => 5
        };

        var scopeClarity = Math.Min(10, candidate.Scope.SpecificityScore / 8.0);
        var explicitBonus = candidate.ExplicitRemember ? 8 : 0;
        var correctionBonus = candidate.IsCorrection ? 8 : 0;
        var evidenceBonus = candidate.HasDeterministicEvidence ? 8 : 0;
        var inferencePenalty = candidate.Trust == SourceTrust.AgentInferred ? 15 : 0;

        return Math.Clamp(
            utility +
            evidence +
            persistence +
            outcomeImpact +
            scopeClarity +
            explicitBonus +
            correctionBonus +
            evidenceBonus -
            inferencePenalty,
            0,
            100);
    }

    private static IReadOnlyList<string> RequiredActions(
        AdmissionDisposition disposition) =>
        disposition switch
        {
            AdmissionDisposition.HumanReview => ["HUMAN_REVIEW"],
            AdmissionDisposition.PendingValidation => ["VALIDATE_SOURCE"],
            AdmissionDisposition.Probation => ["COLLECT_OUTCOME_FEEDBACK"],
            AdmissionDisposition.Quarantine => ["KEEP_OUT_OF_ACTIVE_CONTEXT"],
            _ => []
        };

    private static AdmissionDecision Decision(
        AdmissionDisposition disposition,
        double score,
        string reason) =>
        new(disposition, score, [reason], RequiredActions(disposition));
}
