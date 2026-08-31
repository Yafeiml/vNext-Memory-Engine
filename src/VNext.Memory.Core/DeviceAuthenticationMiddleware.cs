using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VNext.Memory.Domain;

namespace VNext.Memory.Core;

public sealed class DeviceAuthenticationMiddleware(
    RequestDelegate next,
    ILogger<DeviceAuthenticationMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IDeviceIdentityStore identityStore,
        RequestIdentityAccessor identityAccessor,
        IOptions<CoreSecurityOptions> securityOptions)
    {
        if (IsAnonymousPath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var rawToken = ReadBearerToken(context.Request);
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            await WriteUnauthorizedAsync(context, "Missing bearer token.")
                .ConfigureAwait(false);
            return;
        }

        var actorId = HeaderOrDefault(
            context.Request.Headers["X-VME-Actor"].FirstOrDefault(),
            "unknown-agent");
        var sessionId = NormalizeHeader(
            context.Request.Headers["X-VME-Session"].FirstOrDefault());

        var options = securityOptions.Value;
        if (!string.IsNullOrWhiteSpace(options.BootstrapToken) &&
            FixedTimeEquals(rawToken, options.BootstrapToken))
        {
            var tenantId = HeaderOrDefault(
                context.Request.Headers["X-VME-Tenant"].FirstOrDefault(),
                "default");
            var principalId = HeaderOrDefault(
                context.Request.Headers["X-VME-Principal"].FirstOrDefault(),
                "bootstrap-admin");

            identityAccessor.Identity = new RequestIdentity(
                tenantId,
                principalId,
                null,
                actorId,
                sessionId,
                IsAdministrator: true);
            identityAccessor.RawBearerToken = rawToken;

            await next(context).ConfigureAwait(false);
            return;
        }

        var device = await identityStore
            .AuthenticateAsync(HashToken(rawToken), context.RequestAborted)
            .ConfigureAwait(false);

        if (device is null)
        {
            logger.LogWarning(
                "Rejected request with an unknown or inactive edge device token.");
            await WriteUnauthorizedAsync(context, "Invalid device token.")
                .ConfigureAwait(false);
            return;
        }

        identityAccessor.Identity = new RequestIdentity(
            device.TenantId,
            device.PrincipalId,
            device.DeviceId,
            actorId,
            sessionId);
        identityAccessor.RawBearerToken = rawToken;

        await next(context).ConfigureAwait(false);
    }

    private static bool IsAnonymousPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/ready");

    private static string? ReadBearerToken(HttpRequest request)
    {
        var value = request.Headers["Authorization"].FirstOrDefault();
        const string prefix = "Bearer ";

        return value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? value[prefix.Length..].Trim()
            : null;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
        .ToLowerInvariant();

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string HeaderOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeHeader(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task WriteUnauthorizedAsync(
        HttpContext context,
        string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = "unauthorized",
                detail
            },
            context.RequestAborted).ConfigureAwait(false);
    }
}
