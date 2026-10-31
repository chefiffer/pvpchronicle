using System;
using System.Net;
using System.Text.Json;

namespace BattlefieldFrontlineCollector;

internal static class UploadRecoveryPolicy
{
    internal static bool DetailAccessMustBeReestablished(HttpStatusCode statusCode, string body)
    {
        if (statusCode != HttpStatusCode.NotFound || string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False &&
                   root.TryGetProperty("error", out var error) &&
                   string.Equals(error.GetString(), "not found", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A proxy-generated HTML/plain 404 is a routing failure, not an ownership
            // signal, and must never trigger a potentially expensive re-upload.
            return false;
        }
    }
}
