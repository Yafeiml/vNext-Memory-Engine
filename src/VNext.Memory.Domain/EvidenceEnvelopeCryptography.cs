using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VNext.Memory.Domain;

public static class EvidenceEnvelopeCryptography
{
    private const string EvidenceVersion = "vme-evidence-v1";
    private const string FeedbackVersion = "vme-feedback-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static SignedEvidenceEnvelope SignEvidence(
        MemoryRecordRequest observation,
        EvidenceChannel channel,
        string adapter,
        string secret,
        EvidenceProof? proof = null,
        string? adapterVersion = null,
        Guid? eventId = null,
        DateTimeOffset? capturedAt = null,
        string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var id = eventId ?? Guid.NewGuid();
        var timestamp = capturedAt ?? DateTimeOffset.UtcNow;
        var effectiveNonce = string.IsNullOrWhiteSpace(nonce)
            ? CreateNonce()
            : nonce.Trim();
        var payloadHash = ComputeEvidencePayloadHash(
            adapter,
            adapterVersion,
            proof,
            observation);
        var signature = ComputeSignature(
            EvidenceVersion,
            id,
            channel,
            timestamp,
            effectiveNonce,
            payloadHash,
            secret);

        return new SignedEvidenceEnvelope(
            id,
            channel,
            timestamp,
            effectiveNonce,
            adapter.Trim(),
            Normalize(adapterVersion),
            proof,
            observation,
            payloadHash,
            signature);
    }

    public static bool VerifyEvidence(
        SignedEvidenceEnvelope envelope,
        string secret)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var payloadHash = ComputeEvidencePayloadHash(
            envelope.Adapter,
            envelope.AdapterVersion,
            envelope.Proof,
            envelope.Observation);

        if (!FixedTimeHexEquals(payloadHash, envelope.PayloadHash))
        {
            return false;
        }

        var expected = ComputeSignature(
            EvidenceVersion,
            envelope.EventId,
            envelope.Channel,
            envelope.CapturedAt,
            envelope.Nonce,
            payloadHash,
            secret);

        return FixedTimeHexEquals(expected, envelope.Signature);
    }

    public static SignedRetrievalFeedbackEnvelope SignFeedback(
        RetrievalFeedbackRequest feedback,
        EvidenceChannel channel,
        string adapter,
        string secret,
        EvidenceProof? proof = null,
        string? adapterVersion = null,
        Guid? eventId = null,
        DateTimeOffset? capturedAt = null,
        string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var id = eventId ?? Guid.NewGuid();
        var timestamp = capturedAt ?? DateTimeOffset.UtcNow;
        var effectiveNonce = string.IsNullOrWhiteSpace(nonce)
            ? CreateNonce()
            : nonce.Trim();
        var payloadHash = ComputeFeedbackPayloadHash(
            adapter,
            adapterVersion,
            proof,
            feedback);
        var signature = ComputeSignature(
            FeedbackVersion,
            id,
            channel,
            timestamp,
            effectiveNonce,
            payloadHash,
            secret);

        return new SignedRetrievalFeedbackEnvelope(
            id,
            channel,
            timestamp,
            effectiveNonce,
            adapter.Trim(),
            Normalize(adapterVersion),
            proof,
            feedback,
            payloadHash,
            signature);
    }

    public static bool VerifyFeedback(
        SignedRetrievalFeedbackEnvelope envelope,
        string secret)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var payloadHash = ComputeFeedbackPayloadHash(
            envelope.Adapter,
            envelope.AdapterVersion,
            envelope.Proof,
            envelope.Feedback);

        if (!FixedTimeHexEquals(payloadHash, envelope.PayloadHash))
        {
            return false;
        }

        var expected = ComputeSignature(
            FeedbackVersion,
            envelope.EventId,
            envelope.Channel,
            envelope.CapturedAt,
            envelope.Nonce,
            payloadHash,
            secret);

        return FixedTimeHexEquals(expected, envelope.Signature);
    }

    public static string ComputeEvidencePayloadHash(
        string adapter,
        string? adapterVersion,
        EvidenceProof? proof,
        MemoryRecordRequest observation) =>
        Sha256(JsonSerializer.SerializeToUtf8Bytes(
            new EvidencePayload(
                adapter.Trim(),
                Normalize(adapterVersion),
                proof,
                observation),
            JsonOptions));

    public static string ComputeFeedbackPayloadHash(
        string adapter,
        string? adapterVersion,
        EvidenceProof? proof,
        RetrievalFeedbackRequest feedback) =>
        Sha256(JsonSerializer.SerializeToUtf8Bytes(
            new FeedbackPayload(
                adapter.Trim(),
                Normalize(adapterVersion),
                proof,
                feedback),
            JsonOptions));

    public static Guid CreateDeterministicEventId(string material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(material);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    private static string ComputeSignature(
        string version,
        Guid eventId,
        EvidenceChannel channel,
        DateTimeOffset capturedAt,
        string nonce,
        string payloadHash,
        string secret)
    {
        var canonical = string.Join(
            '\n',
            version,
            eventId.ToString("D"),
            ((int)channel).ToString(CultureInfo.InvariantCulture),
            capturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            nonce,
            payloadHash);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string CreateNonce() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool FixedTimeHexEquals(string expected, string supplied)
    {
        try
        {
            var left = Convert.FromHexString(expected);
            var right = Convert.FromHexString(supplied);
            return left.Length == right.Length &&
                   CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record EvidencePayload(
        string Adapter,
        string? AdapterVersion,
        EvidenceProof? Proof,
        MemoryRecordRequest Observation);

    private sealed record FeedbackPayload(
        string Adapter,
        string? AdapterVersion,
        EvidenceProof? Proof,
        RetrievalFeedbackRequest Feedback);
}
