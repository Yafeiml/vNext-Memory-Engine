using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

public sealed class CoreMemoryClient(
    HttpClient httpClient,
    IOptions<EdgeOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly EdgeOptions _options = options.Value;

    public Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        string actorId,
        string? sessionId,
        CancellationToken cancellationToken) =>
        SendAsync<MemoryRecordRequest, RecordMemoryResult>(
            HttpMethod.Post,
            "/api/v1/memories/record",
            request,
            actorId,
            sessionId,
            cancellationToken);

    public Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
        string actorId,
        string? sessionId,
        CancellationToken cancellationToken) =>
        SendAsync<MemorySearchRequest, IReadOnlyList<MemorySearchHit>>(
            HttpMethod.Post,
            "/api/v1/memories/search",
            request,
            actorId,
            sessionId,
            cancellationToken);

    public Task<MemoryContextPacket> CompileContextAsync(
        ContextCompileRequest request,
        string actorId,
        string? sessionId,
        CancellationToken cancellationToken) =>
        SendAsync<ContextCompileRequest, MemoryContextPacket>(
            HttpMethod.Post,
            "/api/v1/context/compile",
            request,
            actorId,
            sessionId,
            cancellationToken);

    public async Task<MemoryExplanation?> ExplainAsync(
        Guid claimId,
        string actorId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        using var message = CreateMessage(
            HttpMethod.Get,
            $"/api/v1/memories/{claimId:D}/explain",
            actorId,
            sessionId);

        using var response = await httpClient
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content
            .ReadFromJsonAsync<MemoryExplanation>(
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest request,
        string actorId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        using var message = CreateMessage(method, path, actorId, sessionId);
        message.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await httpClient
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Memory Core returned an empty response.");
    }

    private HttpRequestMessage CreateMessage(
        HttpMethod method,
        string path,
        string actorId,
        string? sessionId)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.CoreToken);
        message.Headers.TryAddWithoutValidation("X-VME-Actor", actorId);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            message.Headers.TryAddWithoutValidation("X-VME-Session", sessionId);
        }

        message.Headers.TryAddWithoutValidation(
            "X-VME-Tenant",
            _options.TenantId);
        message.Headers.TryAddWithoutValidation(
            "X-VME-Principal",
            _options.PrincipalId);
        return message;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        throw new CoreMemoryRequestException(
            response.StatusCode,
            detail,
            response.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500);
    }
}

public sealed class CoreMemoryRequestException(
    HttpStatusCode statusCode,
    string detail,
    bool retryable) : Exception(
        $"Memory Core request failed with {(int)statusCode}: {detail}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool Retryable { get; } = retryable;
}
