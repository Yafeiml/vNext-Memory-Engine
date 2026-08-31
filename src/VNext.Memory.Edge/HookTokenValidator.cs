using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace VNext.Memory.Edge;

public sealed class HookTokenValidator(IOptions<EdgeOptions> options)
{
    private readonly string _expected = options.Value.HookToken;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_expected);

    public bool Validate(string? supplied)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(_expected);
        var right = Encoding.UTF8.GetBytes(supplied.Trim());
        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(left, right);
    }
}
