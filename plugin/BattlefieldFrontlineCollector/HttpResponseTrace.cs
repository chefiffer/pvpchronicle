using System;
using System.Linq;
using System.Net.Http;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Extracts only non-sensitive correlation identifiers from an HTTP response.
/// Input: response headers controlled by the server or Cloudflare.
/// Output: a short display-safe suffix suitable for local error logs/UI.
/// Authorization headers, request bodies, player identities and IP addresses
/// are intentionally outside this contract.
/// </summary>
internal static class HttpResponseTrace
{
    private const int MaximumIdentifierLength = 80;

    internal static string? From(HttpResponseMessage response)
    {
        var requestId = Header(response, "X-PvPChronicle-Request-Id");
        var cfRay = Header(response, "CF-Ray");
        if (requestId == null && cfRay == null) return null;

        var parts = new[]
        {
            requestId == null ? null : $"request={requestId}",
            cfRay == null ? null : $"cf-ray={cfRay}",
        }.Where(value => value != null);
        return string.Join(", ", parts!);
    }

    internal static string Append(string message, string? trace)
    {
        if (string.IsNullOrWhiteSpace(trace)) return message;
        var suffix = $" [{trace}]";
        const int maximumStoredErrorLength = 240;
        var available = maximumStoredErrorLength - suffix.Length;
        if (message.Length > available)
            message = available > 3 ? message[..(available - 3)] + "..." : string.Empty;
        return message + suffix;
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var value = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaximumIdentifierLength) return null;
        return value.All(character => char.IsAsciiLetterOrDigit(character) ||
                                      character is '-' or '_' or '.' or ':')
            ? value
            : null;
    }
}
