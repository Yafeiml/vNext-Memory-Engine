using System.ComponentModel;
using ModelContextProtocol.Server;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

[McpServerToolType]
public sealed class EdgeMcpTools
{
    [McpServerTool(Name = "memory_remember")]
    [Description(
        "Record an agent observation through the local edge. Client-declared trust is treated as a claim; the Core derives effective trust from the signed capture channel.")]
    public static Task<RecordMemoryResult> RememberAsync(
        IEdgeMemoryGateway gateway,
        [Description("Observation and its scope/provenance metadata.")]
        MemoryRecordRequest request,
        CancellationToken cancellationToken) =>
        gateway.RecordAsync(request, cancellationToken);

    [McpServerTool(Name = "memory_search")]
    [Description(
        "Search governed shared memory through the central Memory Core. Every hit contains a retrieval trace id.")]
    public static Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        IEdgeMemoryGateway gateway,
        [Description("Search query, current scope, and result options.")]
        MemorySearchRequest request,
        CancellationToken cancellationToken) =>
        gateway.SearchAsync(request, cancellationToken);

    [McpServerTool(Name = "memory_search_traced")]
    [Description(
        "Search governed memory and return an explicit trace envelope for later outcome feedback.")]
    public static Task<MemorySearchResponse> SearchTracedAsync(
        IEdgeMemoryGateway gateway,
        MemorySearchRequest request,
        CancellationToken cancellationToken) =>
        gateway.SearchWithTraceAsync(request, cancellationToken);

    [McpServerTool(Name = "memory_feedback")]
    [Description(
        "Record non-authoritative agent feedback about a retrieval trace. Deterministic or user-authoritative feedback must arrive through an assured hook channel.")]
    public static Task<RetrievalFeedbackResult> FeedbackAsync(
        IEdgeMemoryGateway gateway,
        RetrievalFeedbackRequest request,
        CancellationToken cancellationToken) =>
        gateway.RecordFeedbackAsync(request, cancellationToken);

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
        "Return the evidence, source assurance, and admission decisions behind a memory claim.")]
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
