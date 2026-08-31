using VNext.Memory.Domain;

namespace VNext.Memory.Core;

public sealed class RequestIdentityAccessor
{
    public RequestIdentity? Identity { get; set; }

    // Request-scoped only. The raw device token is never persisted by Core.
    public string? RawBearerToken { get; set; }

    public RequestIdentity GetRequiredIdentity() =>
        Identity ?? throw new InvalidOperationException(
            "No authenticated memory request identity is available.");

    public string GetRequiredSigningSecret() =>
        RawBearerToken ?? throw new InvalidOperationException(
            "No request-scoped signing secret is available.");
}
