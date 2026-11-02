using System;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Stable player-facing transfer contract shared by Frontline and Crystalline
/// Conflict. Inputs are legacy/local/server state strings; output is one of six
/// public states. Private queue names and worker failures never enter this API.
/// </summary>
internal static class MatchTransferContract
{
    public static readonly string[] States =
    [
        "waiting_upload", "uploading", "received", "processing", "ready",
        "action_required"
    ];

    public static string Normalize(string? state) => state switch
    {
        "local_only" => "waiting_upload",
        "complete" => "ready",
        "failed" => "action_required",
        "queued" or "archiving" => "received",
        "waiting_upload" or "uploading" or "received" or "processing" or
            "ready" or "action_required" => state,
        _ => "action_required"
    };

    /// <summary>
    /// Server hints are advisory. The client enforces the 15-30 second safety
    /// envelope so a bad or stale server cannot cause a polling storm.
    /// </summary>
    public static int PollDelaySeconds(int? requested, int fallback = 30)
        => Math.Clamp(requested ?? fallback, 15, 30);
}
