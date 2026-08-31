using System.Text.RegularExpressions;

namespace VNext.Memory.Application.Security;

public interface ISecretDetector
{
    SecretDetectionResult Inspect(string content);
}

public sealed record SecretDetectionResult(
    bool ContainsSecret,
    IReadOnlyList<string> ReasonCodes);

public sealed partial class SecretDetector : ISecretDetector
{
    private static readonly (Regex Pattern, string Reason)[] Patterns =
    [
        (PrivateKeyRegex(), "PRIVATE_KEY"),
        (JwtRegex(), "JWT"),
        (ProviderTokenRegex(), "PROVIDER_TOKEN"),
        (CredentialAssignmentRegex(), "CREDENTIAL_ASSIGNMENT"),
        (ChineseCredentialRegex(), "CREDENTIAL_ASSIGNMENT_ZH"),
        (ConnectionStringRegex(), "CONNECTION_STRING_PASSWORD")
    ];

    public SecretDetectionResult Inspect(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new(false, []);
        }

        var reasons = Patterns
            .Where(item => item.Pattern.IsMatch(content))
            .Select(item => item.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new(reasons.Length > 0, reasons);
    }

    [GeneratedRegex(
        "-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{16,}|AKIA[A-Z0-9]{16})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderTokenRegex();

    [GeneratedRegex(
        @"(?i)\b(?:password|passwd|pwd|api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*[""']?[^\s""']{6,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        @"(?:密码|密钥|验证码|访问令牌)\s*[:：=]\s*[^\s，。；;]{4,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChineseCredentialRegex();

    [GeneratedRegex(
        @"(?i)(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis)://[^:\s/]+:[^@\s/]+@",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringRegex();
}
