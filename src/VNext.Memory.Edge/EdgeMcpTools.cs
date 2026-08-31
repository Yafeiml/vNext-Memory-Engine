using System.ComponentModel;
using ModelContextProtocol.Server;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

[McpServerToolType]
public sealed class EdgeMcpTools
{
    [McpServerTool(Name = "memory_remember")]
    [Description(
        "Record an observation through the local edge. If the core is offline, the observation is queued in SQLite and synchronized later.")]
    public static Task<RecordMemoryResult> RememberAsync(
        IEdgeMemoryGateway gateway,
        [Description("Observation and its scope/provenance metadata.")]
        MemoryRecordRequest request,
        CancellationToken cancellationToken) =>
        gateway.RecordAsync(request, cancellationToken);

    [McpServerTool(Name = "memory_search")]
    [Description(
        "Search governed shared memory through the central Memory Core.")]
    public static Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        IEdgeMemoryGateway gateway,
        [Description("Search query, current scope, and result options.")]
        MemorySearchRequest request,
        CancellationToken cancellationToken) =>
        gateway.SearchAsync(request, cancellationToken);

    [McpServerTool(Name = "memory_context")]
    [Description(
        "Compile a small, task-specific context packet from shared memory.")]
    public static Task<MemoryContextPacket> ContextAsync(
        IEdgeMemoryGateway gateway,
        [Description("Task, scope, files, and token budget.")]
        ContextCompileRequest request,
        CancellationToken cancellationToken) =>
        gateway.CompileContextAsync(request, cancellationToken);

    [McpServerTool(Name = "memory_explain")]
    [Description(
        "Return the evidence and admission decisions behind a memory claim.")]
    public static Task<MemoryExplanation?> ExplainAsync(
        IEdgeMemoryGateway gateway,
        Guid claimId,
        CancellationToken cancellationToken) =>
        gateway.ExplainAsync(
            claimId,
            null,
            null,
            cancellationToken);
}
