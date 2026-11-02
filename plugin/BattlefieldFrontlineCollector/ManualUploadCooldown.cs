using System;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Shared, persisted manual-upload gate for both PvP modes. It only controls
/// explicit UI clicks; the durable automatic-upload and retry queues remain
/// responsible for eventual delivery after a network failure.
/// </summary>
internal static class ManualUploadCooldown
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    internal static TimeSpan Remaining(DateTime deadlineUtc, DateTime nowUtc)
    {
        var normalizedNow = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc : nowUtc.ToUniversalTime();
        var normalizedDeadline = deadlineUtc.Kind == DateTimeKind.Utc
            ? deadlineUtc : deadlineUtc.ToUniversalTime();
        var remaining = normalizedDeadline - normalizedNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal static DateTime Acquire(DateTime nowUtc)
    {
        var normalizedNow = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc : nowUtc.ToUniversalTime();
        return normalizedNow + Duration;
    }
}
