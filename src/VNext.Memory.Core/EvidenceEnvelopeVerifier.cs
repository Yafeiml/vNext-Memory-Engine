using VNext.Memory.Domain;

namespace VNext.Memory.Core;

public sealed class EvidenceEnvelopeVerifier
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    public SourceAssertionContext VerifyEvidence(
        SignedEvidenceEnvelope envelope,
        RequestIdentityAccessor identityAccessor)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateCommon(
            envelope.EventId,
            envelope.Channel,
            envelope.CapturedAt,
            envelope.Nonce,
            envelope.Adapter);

        if (!EvidenceEnvelopeCryptography.VerifyEvidence(
                envelope,
                identityAccessor.GetRequiredSigningSecret()))
        {
            throw new EvidenceEnvelopeValidationException(
                "INVALID_EVIDENCE_SIGNATURE",
                "The evidence envelope signature or payload hash is invalid.",
                StatusCodes.Status401Unauthorized);
        }

        return new SourceAssertionContext(
            envelope.Channel,
            AssuranceLevel.Signed,
            envelope.Observation.Trust,
            SignatureVerified: true,
            envelope.Adapter,
            envelope.AdapterVersion,
            envelope.Proof,
            envelope.EventId,
            envelope.PayloadHash,
            ["DEVICE_HMAC_VERIFIED", "PAYLOAD_HASH_VERIFIED"]);
    }

    public SourceAssertionContext VerifyFeedback(
        SignedRetrievalFeedbackEnvelope envelope,
        RequestIdentityAccessor identityAccessor)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateCommon(
            envelope.EventId,
            envelope.Channel,
            envelope.CapturedAt,
            envelope.Nonce,
            envelope.Adapter);

        if (!EvidenceEnvelopeCryptography.VerifyFeedback(
                envelope,
                identityAccessor.GetRequiredSigningSecret()))
        {
            throw new EvidenceEnvelopeValidationException(
                "INVALID_FEEDBACK_SIGNATURE",
                "The retrieval feedback signature or payload hash is invalid.",
                StatusCodes.Status401Unauthorized);
        }

        return new SourceAssertionContext(
            envelope.Channel,
            AssuranceLevel.Signed,
            ClaimedTrustFor(envelope.Channel),
            SignatureVerified: true,
            envelope.Adapter,
            envelope.AdapterVersion,
            envelope.Proof,
            envelope.EventId,
            envelope.PayloadHash,
            ["DEVICE_HMAC_VERIFIED", "FEEDBACK_PAYLOAD_HASH_VERIFIED"]);
    }

    private static void ValidateCommon(
        Guid eventId,
        EvidenceChannel channel,
        DateTimeOffset capturedAt,
        string nonce,
        string adapter)
    {
        if (eventId == Guid.Empty)
        {
            throw Invalid("EVENT_ID_REQUIRED", "A non-empty event id is required.");
        }

        if (channel == EvidenceChannel.LegacyClient)
        {
            throw Invalid(
                "SIGNED_LEGACY_CHANNEL_NOT_ALLOWED",
                "Signed envelopes must identify their actual capture channel.");
        }

        if (string.IsNullOrWhiteSpace(adapter))
        {
            throw Invalid("ADAPTER_REQUIRED", "An adapter name is required.");
        }

        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length is < 16 or > 128)
        {
            throw Invalid("INVALID_NONCE", "The envelope nonce is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        if (capturedAt > now + MaximumFutureSkew)
        {
            throw Invalid(
                "CAPTURE_TIME_IN_FUTURE",
                "The envelope capture time is too far in the future.");
        }

        if (capturedAt < now - MaximumAge)
        {
            throw Invalid(
                "CAPTURE_TIME_TOO_OLD",
                "The envelope is older than the accepted offline replay window.");
        }
    }

    private static SourceTrust ClaimedTrustFor(EvidenceChannel channel) =>
        channel switch
        {
            EvidenceChannel.UserMessage => SourceTrust.UserExplicit,
            EvidenceChannel.ToolResult or EvidenceChannel.TestResult =>
                SourceTrust.ToolObserved,
            EvidenceChannel.CodeArtifact or EvidenceChannel.GitResult =>
                SourceTrust.CodeDerived,
            EvidenceChannel.ExternalContent => SourceTrust.ExternalUntrusted,
            EvidenceChannel.Administrative => SourceTrust.HumanApproved,
            _ => SourceTrust.AgentInferred
        };

    private static EvidenceEnvelopeValidationException Invalid(
        string code,
        string message) =>
        new(code, message, StatusCodes.Status400BadRequest);
}

public sealed class EvidenceEnvelopeValidationException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
