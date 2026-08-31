using System.Text;

namespace VNext.Memory.Domain;

public sealed record MemoryScope
{
    public string? TenantId { get; init; }
    public string? UserId { get; init; }
    public string? ProjectId { get; init; }
    public string? RepositoryId { get; init; }
    public string? Branch { get; init; }
    public string? WorktreeId { get; init; }
    public string? Environment { get; init; }
    public string? TaskId { get; init; }

    public MemoryScope Normalize(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return this with
        {
            TenantId = NormalizeValue(tenantId),
            UserId = NormalizeValue(UserId),
            ProjectId = NormalizeValue(ProjectId),
            RepositoryId = NormalizeRepository(RepositoryId),
            Branch = NormalizeValue(Branch),
            WorktreeId = NormalizeValue(WorktreeId),
            Environment = NormalizeValue(Environment),
            TaskId = NormalizeValue(TaskId)
        };
    }

    public int SpecificityScore =>
        Score(TenantId, 1) +
        Score(UserId, 10) +
        Score(ProjectId, 25) +
        Score(RepositoryId, 30) +
        Score(Branch, 20) +
        Score(WorktreeId, 15) +
        Score(Environment, 15) +
        Score(TaskId, 25);

    public bool IsProjectScoped =>
        !string.IsNullOrWhiteSpace(ProjectId) ||
        !string.IsNullOrWhiteSpace(RepositoryId);

    public bool IsUserScoped => !string.IsNullOrWhiteSpace(UserId);

    public string ToCanonicalString()
    {
        var builder = new StringBuilder(256);
        Append(builder, nameof(TenantId), TenantId);
        Append(builder, nameof(UserId), UserId);
        Append(builder, nameof(ProjectId), ProjectId);
        Append(builder, nameof(RepositoryId), RepositoryId);
        Append(builder, nameof(Branch), Branch);
        Append(builder, nameof(WorktreeId), WorktreeId);
        Append(builder, nameof(Environment), Environment);
        Append(builder, nameof(TaskId), TaskId);
        return builder.ToString();
    }

    private static int Score(string? value, int weight) =>
        string.IsNullOrWhiteSpace(value) ? 0 : weight;

    private static string? NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeRepository(string? value)
    {
        var normalized = NormalizeValue(value);
        if (normalized is null)
        {
            return null;
        }

        normalized = normalized
            .Replace('\\', '/')
            .TrimEnd('/');

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string name, string? value)
    {
        builder.Append(name)
            .Append('=')
            .Append(value ?? "*")
            .Append(';');
    }
}
