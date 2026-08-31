using System.Text.RegularExpressions;
using VNext.Memory.Domain;

namespace VNext.Memory.Application.Security;

public interface IPermissionRiskDetector
{
    bool IsHighRisk(MemoryCandidate candidate);
}

public sealed partial class PermissionRiskDetector : IPermissionRiskDetector
{
    public bool IsHighRisk(MemoryCandidate candidate)
    {
        if (candidate.Kind is not (
            MemoryKind.Decision or
            MemoryKind.Procedure or
            MemoryKind.Constraint))
        {
            return false;
        }

        return PermissionRegex().IsMatch(candidate.Statement);
    }

    [GeneratedRegex(
        @"(?ix)
        (?:无需|不用|跳过).{0,10}(?:确认|审批|授权) |
        (?:自动|直接).{0,12}(?:部署.{0,4}生产|删除.{0,6}(?:数据库|数据)|付款|支付|发送邮件|修改权限) |
        (?:authorize|allow|permit).{0,20}(?:without\s+(?:approval|confirmation)|production\s+deploy|delete\s+(?:the\s+)?database|payment) |
        (?:bypass|skip).{0,12}(?:approval|confirmation)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PermissionRegex();
}
