using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

public sealed record HookIngestResponse(
    string Provider,
    string EventName,
    int ObservationCount,
    IReadOnlyList<RecordMemoryResult> Results);

public sealed partial class HookIngestionService(
    IEdgeMemoryGateway gateway,
    ILogger<HookIngestionService> logger)
{
    public async Task<HookIngestResponse> IngestAsync(
        string provider,
        JsonElement hookEvent,
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var captures = normalizedProvider switch
        {
            "claude" or "claude-code" => NormalizeClaude(hookEvent, scope),
            "codex" => NormalizeCodex(hookEvent, scope),
            _ => NormalizeGeneric(normalizedProvider, hookEvent, scope)
        };

        var results = new List<RecordMemoryResult>(captures.Count);
        foreach (var capture in captures)
        {
            results.Add(await gateway
                .RecordCapturedAsync(capture, cancellationToken)
                .ConfigureAwait(false));
        }

        var eventName = GetString(hookEvent, "hook_event_name") ??
                        GetString(hookEvent, "type") ??
                        "unknown";
        logger.LogInformation(
            "Ingested {Count} observations from {Provider} hook event {EventName}.",
            captures.Count,
            normalizedProvider,
            eventName);

        return new HookIngestResponse(
            normalizedProvider,
            eventName,
            captures.Count,
            results);
    }

    private static IReadOnlyList<CapturedObservation> NormalizeClaude(
        JsonElement input,
        MemoryScope scope)
    {
        var captures = new List<CapturedObservation>();
        var eventName = GetString(input, "hook_event_name") ?? "unknown";
        var sessionId = GetString(input, "session_id");
        var promptId = GetString(input, "prompt_id");
        var toolUseId = GetString(input, "tool_use_id");
        var actorId = GetString(input, "agent_type") ?? "claude-code";
        var discriminator = promptId ?? toolUseId ?? Sha256(input.GetRawText())[..16];

        if (string.Equals(
                eventName,
                "UserPromptSubmit",
                StringComparison.OrdinalIgnoreCase))
        {
            var prompt = GetString(input, "prompt");
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                var explicitRemember = IsExplicitMemoryRequest(prompt);
                var correction = IsCorrection(prompt);
                captures.Add(Capture(
                    new MemoryRecordRequest
                    {
                        Content = Limit(prompt, 4_000),
                        Kind = explicitRemember && LooksLikePreference(prompt)
                            ? MemoryKind.Preference
                            : MemoryKind.Note,
                        Trust = SourceTrust.UserExplicit,
                        ExplicitRemember = explicitRemember,
                        IsCorrection = correction,
                        Scope = scope,
                        SourceType = "claude-code:UserPromptSubmit",
                        SourceReference = $"claude:{sessionId}:{discriminator}",
                        AgentId = actorId,
                        SessionId = sessionId
                    },
                    EvidenceChannel.UserMessage,
                    "claude-code-hook",
                    eventName,
                    sessionId,
                    discriminator,
                    proof: new EvidenceProof(ProviderEventType: eventName)));
            }

            return captures;
        }

        if (eventName is "PostToolUse" or "PostToolUseFailure")
        {
            var toolName = GetString(input, "tool_name") ?? "unknown-tool";
            var command = GetNestedString(input, "tool_input", "command");
            var failed = eventName == "PostToolUseFailure";
            var error = GetString(input, "error");
            var response = GetPropertyText(input, "tool_response");
            var exitCode = failed
                ? ParseExitCode(error)
                : IsShellTool(toolName) ? 0 : null;
            var channel = ClassifyToolChannel(toolName, command);
            var validationCommand = IsValidationCommand(command);
            var deterministic = exitCode.HasValue &&
                                (validationCommand ||
                                 channel is EvidenceChannel.TestResult or
                                     EvidenceChannel.GitResult);
            var kind = failed && deterministic
                ? MemoryKind.FailedApproach
                : !failed && deterministic
                    ? MemoryKind.TechnicalFact
                    : MemoryKind.Note;
            var summary = failed
                ? $"Tool {toolName} failed{FormatCommand(command)}. Error: {Limit(error ?? "unknown error", 1_200)}"
                : $"Tool {toolName} completed successfully{FormatCommand(command)}. Result: {Limit(response ?? "success", 1_200)}";
            var proof = new EvidenceProof(
                ProviderEventType: eventName,
                ToolName: toolName,
                Command: command,
                ExitCode: exitCode,
                ArtifactDigest: channel == EvidenceChannel.CodeArtifact
                    ? Sha256(input.GetRawText())
                    : null,
                TestRunId: channel == EvidenceChannel.TestResult
                    ? toolUseId
                    : null);

            captures.Add(Capture(
                new MemoryRecordRequest
                {
                    Content = Limit(summary, 4_000),
                    Kind = kind,
                    Trust = SourceTrust.ToolObserved,
                    HasDeterministicEvidence = deterministic,
                    Scope = scope,
                    SourceType = $"claude-code:{eventName}",
                    SourceReference = $"claude:{sessionId}:{toolUseId ?? discriminator}",
                    AgentId = actorId,
                    SessionId = sessionId,
                    Tags = ["hook", "claude-code", toolName.ToLowerInvariant()]
                },
                channel,
                "claude-code-hook",
                eventName,
                sessionId,
                toolUseId ?? discriminator,
                proof));

            return captures;
        }

        if (string.Equals(eventName, "Stop", StringComparison.OrdinalIgnoreCase))
        {
            var message = GetString(input, "last_assistant_message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                captures.Add(Capture(
                    new MemoryRecordRequest
                    {
                        Content = Limit(message, 4_000),
                        Kind = MemoryKind.Note,
                        Trust = SourceTrust.AgentInferred,
                        Scope = scope,
                        SourceType = "claude-code:Stop",
                        SourceReference = $"claude:{sessionId}:{discriminator}",
                        AgentId = actorId,
                        SessionId = sessionId
                    },
                    EvidenceChannel.AgentObservation,
                    "claude-code-hook",
                    eventName,
                    sessionId,
                    discriminator,
                    proof: new EvidenceProof(ProviderEventType: eventName)));
            }
        }

        return captures;
    }

    private static IReadOnlyList<CapturedObservation> NormalizeCodex(
        JsonElement input,
        MemoryScope scope)
    {
        var captures = new List<CapturedObservation>();
        var eventName = GetString(input, "type") ?? "unknown";
        var threadId = GetString(input, "thread-id") ??
                       GetString(input, "thread_id") ??
                       "unknown-thread";
        var turnId = GetString(input, "turn-id") ??
                     GetString(input, "turn_id") ??
                     Sha256(input.GetRawText())[..16];
        var sessionId = threadId;
        var actorId = GetString(input, "client") ?? "codex";

        if (TryGetProperty(input, "input-messages", out var messages) ||
            TryGetProperty(input, "input_messages", out messages))
        {
            if (messages.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in messages.EnumerateArray())
                {
                    var message = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.GetRawText();
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    var explicitRemember = IsExplicitMemoryRequest(message);
                    var discriminator = $"{turnId}:input:{index++}";
                    captures.Add(Capture(
                        new MemoryRecordRequest
                        {
                            Content = Limit(message, 4_000),
                            Kind = explicitRemember && LooksLikePreference(message)
                                ? MemoryKind.Preference
                                : MemoryKind.Note,
                            Trust = SourceTrust.UserExplicit,
                            ExplicitRemember = explicitRemember,
                            IsCorrection = IsCorrection(message),
                            Scope = scope,
                            SourceType = "codex:agent-turn-input",
                            SourceReference = $"codex:{threadId}:{discriminator}",
                            AgentId = actorId,
                            SessionId = sessionId
                        },
                        EvidenceChannel.UserMessage,
                        "codex-legacy-notify",
                        eventName,
                        sessionId,
                        discriminator,
                        proof: new EvidenceProof(ProviderEventType: eventName)));
                }
            }
        }

        var assistantMessage = GetString(input, "last-assistant-message") ??
                               GetString(input, "last_assistant_message");
        if (!string.IsNullOrWhiteSpace(assistantMessage))
        {
            captures.Add(Capture(
                new MemoryRecordRequest
                {
                    Content = Limit(assistantMessage, 4_000),
                    Kind = MemoryKind.Note,
                    Trust = SourceTrust.AgentInferred,
                    Scope = scope,
                    SourceType = "codex:agent-turn-complete",
                    SourceReference = $"codex:{threadId}:{turnId}:assistant",
                    AgentId = actorId,
                    SessionId = sessionId
                },
                EvidenceChannel.AgentObservation,
                "codex-legacy-notify",
                eventName,
                sessionId,
                $"{turnId}:assistant",
                proof: new EvidenceProof(ProviderEventType: eventName)));
        }

        return captures;
    }

    private static IReadOnlyList<CapturedObservation> NormalizeGeneric(
        string provider,
        JsonElement input,
        MemoryScope scope)
    {
        var eventName = GetString(input, "event") ??
                        GetString(input, "type") ??
                        "unknown";
        var sessionId = GetString(input, "session_id");
        var raw = Limit(input.GetRawText(), 4_000);
        return
        [
            Capture(
                new MemoryRecordRequest
                {
                    Content = raw,
                    Kind = MemoryKind.Note,
                    Trust = SourceTrust.AgentInferred,
                    Scope = scope,
                    SourceType = $"{provider}:{eventName}",
                    SourceReference = $"{provider}:{sessionId}:{Sha256(raw)[..16]}",
                    AgentId = provider,
                    SessionId = sessionId
                },
                EvidenceChannel.AgentObservation,
                $"{provider}-generic-hook",
                eventName,
                sessionId,
                Sha256(raw)[..16],
                proof: new EvidenceProof(ProviderEventType: eventName))
        ];
    }

    private static CapturedObservation Capture(
        MemoryRecordRequest observation,
        EvidenceChannel channel,
        string adapter,
        string eventName,
        string? sessionId,
        string discriminator,
        EvidenceProof? proof) =>
        new(
            observation,
            channel,
            adapter,
            "1",
            proof,
            EvidenceEnvelopeCryptography.CreateDeterministicEventId(
                string.Join(
                    '\n',
                    adapter,
                    eventName,
                    sessionId ?? string.Empty,
                    discriminator)),
            DateTimeOffset.UtcNow);

    private static EvidenceChannel ClassifyToolChannel(
        string toolName,
        string? command)
    {
        var normalizedTool = toolName.ToLowerInvariant();
        var normalizedCommand = command?.ToLowerInvariant() ?? string.Empty;

        if (IsTestCommand(normalizedCommand))
        {
            return EvidenceChannel.TestResult;
        }

        if (normalizedCommand.StartsWith("git ", StringComparison.Ordinal) ||
            normalizedTool.Contains("git", StringComparison.Ordinal))
        {
            return EvidenceChannel.GitResult;
        }

        if (normalizedTool is "write" or "edit" or "notebookedit" ||
            normalizedTool.Contains("write", StringComparison.Ordinal) ||
            normalizedTool.Contains("edit", StringComparison.Ordinal))
        {
            return EvidenceChannel.CodeArtifact;
        }

        return EvidenceChannel.ToolResult;
    }

    private static bool IsValidationCommand(string? command)
    {
        var value = command?.ToLowerInvariant() ?? string.Empty;
        return IsTestCommand(value) ||
               value.Contains(" build", StringComparison.Ordinal) ||
               value.StartsWith("dotnet build", StringComparison.Ordinal) ||
               value.Contains(" lint", StringComparison.Ordinal) ||
               value.StartsWith("cargo check", StringComparison.Ordinal) ||
               value.StartsWith("go vet", StringComparison.Ordinal) ||
               value.Contains(" compile", StringComparison.Ordinal);
    }

    private static bool IsTestCommand(string command) =>
        command.Contains("dotnet test", StringComparison.Ordinal) ||
        command.Contains("pytest", StringComparison.Ordinal) ||
        command.Contains("npm test", StringComparison.Ordinal) ||
        command.Contains("pnpm test", StringComparison.Ordinal) ||
        command.Contains("yarn test", StringComparison.Ordinal) ||
        command.Contains("cargo test", StringComparison.Ordinal) ||
        command.Contains("go test", StringComparison.Ordinal) ||
        command.Contains("mvn test", StringComparison.Ordinal) ||
        command.Contains("gradle test", StringComparison.Ordinal) ||
        command.Contains("ctest", StringComparison.Ordinal);

    private static bool IsShellTool(string toolName) =>
        toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("PowerShell", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("Shell", StringComparison.OrdinalIgnoreCase);

    private static int? ParseExitCode(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var match = ExitCodeRegex().Match(error);
        return match.Success && int.TryParse(match.Groups[1].Value, out var exitCode)
            ? exitCode
            : null;
    }

    private static bool IsExplicitMemoryRequest(string value) =>
        ContainsAny(
            value,
            "记住",
            "请记住",
            "以后",
            "始终",
            "总是",
            "默认",
            "remember",
            "from now on",
            "always",
            "prefer");

    private static bool IsCorrection(string value) =>
        ContainsAny(
            value,
            "纠正",
            "不对",
            "不是",
            "实际上",
            "应该是",
            "correction",
            "that's wrong",
            "actually");

    private static bool LooksLikePreference(string value) =>
        ContainsAny(
            value,
            "偏好",
            "喜欢",
            "回答",
            "语言",
            "格式",
            "风格",
            "默认",
            "prefer",
            "response",
            "language",
            "format",
            "style");

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string FormatCommand(string? command) =>
        string.IsNullOrWhiteSpace(command)
            ? string.Empty
            : $" while executing `{Limit(command, 500)}`";

    private static string? GetNestedString(
        JsonElement element,
        string objectName,
        string propertyName) =>
        TryGetProperty(element, objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : null;

    private static string? GetPropertyText(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string Limit(string value, int maxLength)
    {
        var normalized = WhitespaceRegex().Replace(value.Trim(), " ");
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    [GeneratedRegex(@"^Exit code\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitCodeRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
