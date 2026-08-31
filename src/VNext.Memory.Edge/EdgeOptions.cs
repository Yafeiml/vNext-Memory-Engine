namespace VNext.Memory.Edge;

public sealed class EdgeOptions
{
    public const string SectionName = "Edge";

    public string CoreUrl { get; set; } = "http://localhost:7337";
    public string CoreToken { get; set; } = string.Empty;
    public string TenantId { get; set; } = "default";
    public string PrincipalId { get; set; } = "local-user";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string DefaultActorId { get; set; } = "local-agent";
    public string OutboxPath { get; set; } = "data/edge-outbox.db";
    public int SyncIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 50;
}
