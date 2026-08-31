using VNext.Memory.Domain;

namespace VNext.Memory.Core;

public sealed class RequestIdentityAccessor
{
    public RequestIdentity? Identity { get; set; }

    public RequestIdentity GetRequiredIdentity() =>
        Identity ?? throw new InvalidOperationException(
            "No authenticated memory request identity is available.");
}
