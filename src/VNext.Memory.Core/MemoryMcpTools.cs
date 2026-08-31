using System.ComponentModel;
using ModelContextProtocol.Server;
using VNext.Memory.Domain;

namespace VNext.Memory.Core;

[McpServerToolType]
public sealed class MemoryMcpTools
{
    [McpServerTool(Name = "memory_remember")]
    [Description(
        "Record an observation. The memory governor automatically decides whether it becomes active memory, probation memory, evidence only, quarantine, or rejection.")]
    public static Task<RecordMemoryResult> RememberAsync(
        IMemoryService memoryService,
        RequestIdentityAccessor identityAccessor,
        [Description("Observation and its scope/provenance metadata.")]
        MemoryRecordRequest request,
        CancellationToken cancellationToken) =>
        memoryService.RecordAsync(
            request,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);

    [McpServerTool(Name = "memory_search")]
    [Description(
        "Search governed memories that apply to the current user, project, repository, branch, environment, or task.")]
    public static Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        IMemoryService memoryService,
        RequestIdentityAccessor identityAccessor,
        [Description("Search query, scope filters, memory kinds, and result limit.")]
        MemorySearchRequest request,
        CancellationToken cancellationToken) =>
        memoryService.SearchAsync(
            request,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);

    [McpServerTool(Name = "memory_context")]
    [Description(
        "Compile a compact task-specific context packet instead of loading the complete memory store.")]
    public static Task<MemoryContextPacket> ContextAsync(
        IMemoryService memoryService,
        RequestIdentityAccessor identityAccessor,
        [Description("Task, scope, files, and token budget for context compilation.")]
        ContextCompileRequest request,
        CancellationToken cancellationToken) =>
        memoryService.CompileContextAsync(
            request,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);

    [McpServerTool(Name = "memory_explain")]
    [Description(
        "Explain why a memory exists by returning its source evidence and admission history.")]
    public static Task<MemoryExplanation?> ExplainAsync(
        IMemoryService memoryService,
        RequestIdentityAccessor identityAccessor,
        [Description("Claim identifier returned by memory_search.")]
        Guid claimId,
        CancellationToken cancellationToken) =>
        memoryService.ExplainAsync(
            claimId,
            identityAccessor.GetRequiredIdentity(),
            cancellationToken);
}
