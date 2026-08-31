namespace VNext.Memory.Domain;

public enum MemoryKind
{
    Preference = 0,
    TechnicalFact = 1,
    FailedApproach = 2,
    Decision = 3,
    Procedure = 4,
    Constraint = 5,
    Hypothesis = 6,
    TaskState = 7,
    ExternalContent = 8,
    Note = 9
}

public enum SourceTrust
{
    ExternalUntrusted = 10,
    AgentInferred = 30,
    UserExplicit = 60,
    ToolObserved = 70,
    CodeDerived = 75,
    UserCorrection = 80,
    HumanApproved = 90,
    PolicyManaged = 100
}

public enum EvidenceChannel
{
    LegacyClient = 0,
    AgentObservation = 1,
    UserMessage = 2,
    ToolResult = 3,
    CodeArtifact = 4,
    TestResult = 5,
    GitResult = 6,
    ExternalContent = 7,
    Administrative = 8,
    OutcomeFeedback = 9
}

public enum AssuranceLevel
{
    Unverified = 0,
    Authenticated = 1,
    Signed = 2,
    Deterministic = 3,
    HumanAttested = 4,
    PolicyManaged = 5
}

public enum RetrievalOutcome
{
    Helpful = 0,
    Irrelevant = 1,
    Confirmed = 2,
    Contradicted = 3,
    Harmful = 4
}

public enum AdmissionDisposition
{
    Reject = 0,
    EvidenceOnly = 1,
    Quarantine = 2,
    PendingValidation = 3,
    Probation = 4,
    Active = 5,
    HumanReview = 6
}

public enum MemoryStatus
{
    Probation = 0,
    Active = 1,
    Stale = 2,
    Superseded = 3,
    Revoked = 4,
    Archived = 5,
    Quarantined = 6,
    PendingReview = 7
}

public enum ResolutionPolicy
{
    MostSpecificOverride = 0,
    TemporalLatest = 1,
    EvidenceWeighted = 2,
    DenyWins = 3,
    SetUnion = 4,
    KeepConflicts = 5,
    AppendOnly = 6,
    TransactionalLatest = 7
}
