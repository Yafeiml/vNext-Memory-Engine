using VNext.Memory.Domain;

namespace VNext.Memory.Application.Scope;

public sealed class ScopeResolver : IScopeResolver
{
    public MemoryScope InferAndNormalizeScope(
        MemoryRecordRequest request,
        RequestIdentity identity)
    {
        var scope = request.Scope.Normalize(identity.TenantId);

        if (request.Kind == MemoryKind.Preference &&
            (string.IsNullOrWhiteSpace(scope.UserId) || !identity.IsAdministrator))
        {
            scope = scope with { UserId = identity.PrincipalId };
        }
        else if (!identity.IsAdministrator &&
                 !string.IsNullOrWhiteSpace(scope.UserId) &&
                 !string.Equals(
                     scope.UserId,
                     identity.PrincipalId,
                     StringComparison.OrdinalIgnoreCase))
        {
            scope = scope with { UserId = identity.PrincipalId };
        }

        return scope;
    }

    public bool IsApplicable(
        MemoryScope memoryScope,
        MemoryScope queryScope)
    {
        var memory = memoryScope.Normalize(
            memoryScope.TenantId ?? queryScope.TenantId ?? "default");
        var query = queryScope.Normalize(
            queryScope.TenantId ?? memory.TenantId ?? "default");

        if (!EqualsValue(memory.TenantId, query.TenantId))
        {
            return false;
        }

        return MatchesOptional(memory.UserId, query.UserId) &&
               MatchesOptional(memory.ProjectId, query.ProjectId) &&
               MatchesOptional(memory.RepositoryId, query.RepositoryId) &&
               MatchesOptional(memory.Branch, query.Branch) &&
               MatchesOptional(memory.WorktreeId, query.WorktreeId) &&
               MatchesOptional(memory.Environment, query.Environment) &&
               MatchesOptional(memory.TaskId, query.TaskId);
    }

    public double CalculateMatchScore(
        MemoryScope memoryScope,
        MemoryScope queryScope)
    {
        if (!IsApplicable(memoryScope, queryScope))
        {
            return double.NegativeInfinity;
        }

        var score = memoryScope.SpecificityScore;

        if (!string.IsNullOrWhiteSpace(memoryScope.TaskId))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(memoryScope.Branch))
        {
            score += 10;
        }

        return score;
    }

    public IReadOnlyList<MemoryClaim> Resolve(
        IEnumerable<MemoryClaim> claims,
        MemoryScope queryScope,
        int limit)
    {
        var applicable = claims
            .Where(claim => IsApplicable(claim.Scope, queryScope))
            .Select(claim => new
            {
                Claim = claim,
                ScopeScore = CalculateMatchScore(claim.Scope, queryScope)
            })
            .OrderByDescending(item => item.ScopeScore)
            .ThenByDescending(item => item.Claim.Status == MemoryStatus.Active)
            .ThenByDescending(item => item.Claim.Confidence)
            .ThenByDescending(item => item.Claim.TextScore)
            .ThenByDescending(item => item.Claim.UpdatedAt)
            .ToList();

        var resolved = new List<MemoryClaim>(limit);
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in applicable)
        {
            var claim = item.Claim;
            var usesOverride = claim.ResolutionPolicy is
                ResolutionPolicy.MostSpecificOverride or
                ResolutionPolicy.TemporalLatest;

            if (usesOverride && !string.IsNullOrWhiteSpace(claim.MemoryKey))
            {
                var key = $"{claim.Kind}:{claim.MemoryKey}";
                if (!selectedKeys.Add(key))
                {
                    continue;
                }
            }

            resolved.Add(claim);
            if (resolved.Count >= limit)
            {
                break;
            }
        }

        return resolved;
    }

    private static bool MatchesOptional(string? memoryValue, string? queryValue) =>
        string.IsNullOrWhiteSpace(memoryValue) ||
        (!string.IsNullOrWhiteSpace(queryValue) &&
         EqualsValue(memoryValue, queryValue));

    private static bool EqualsValue(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
