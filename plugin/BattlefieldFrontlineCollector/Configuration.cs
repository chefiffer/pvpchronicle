using Dalamud.Configuration;
using System;
using System.Security.Cryptography;
using System.Text;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// User-owned settings. Secrets are never written into a match export.
/// 用户设置；上传令牌绝不会写入战局导出文件。
/// </summary>
internal sealed class Configuration : IPluginConfiguration
{
    private const string ProtectedPrefix = "dpapi:v1:";
    private static readonly byte[] TokenEntropy =
        Encoding.UTF8.GetBytes("BattlefieldFrontlineCollector.UploadToken.v1");

    public int Version { get; set; } = 13;
    public bool AutoUpload { get; set; }
    public bool OpenAfterMatch { get; set; } = true;
    // Navigation preferences only. Hidden modes remain safely captured and
    // their existing local records are never deleted.
    public bool ShowFrontline { get; set; } = true;
    public bool ShowCrystallineConflict { get; set; } = true;
    public bool ShowRivalWings { get; set; }
    public string Realm { get; set; } = "CN";
    // Kept for one migration cycle so v0.2 settings do not silently disappear.
    public string? Region { get; set; }
    public string ServerBaseUrl { get; set; } = "https://api.pvpchronicle.com";
    // Serialized value only. Version 9 stores DPAPI ciphertext here; callers
    // must use GetCollectorToken/SetCollectorToken and never export this field.
    public string CollectorToken { get; set; } = string.Empty;
    // Used only when Windows MachineGuid is unavailable (for example, an
    // unusual compatibility environment). It contains no game account data.
    public string MachineFallbackId { get; set; } = string.Empty;
    public string Language { get; set; } = "auto";
    public string UiTheme { get; set; } = "night";

    public void Migrate()
    {
        if (Version < 2 && !string.IsNullOrWhiteSpace(Region))
            Realm = Region.Equals("cn", System.StringComparison.OrdinalIgnoreCase) ? "CN" : "JP";
        // Public builds use the HTTPS service by default. Historical private
        // network migration addresses are intentionally not part of the
        // public source tree, and user-supplied endpoints are never rewritten.
        if (Realm is not ("CN" or "JP" or "NA" or "EU" or "OCE")) Realm = "CN";
        if (UiTheme is not ("day" or "night" or "eye" or "contrast")) UiTheme = "night";
        if (Version < 9 && !string.IsNullOrWhiteSpace(CollectorToken) &&
            !CollectorToken.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            SetCollectorToken(CollectorToken);
        if (string.IsNullOrWhiteSpace(MachineFallbackId))
            MachineFallbackId = Guid.NewGuid().ToString("N");
        Version = 13;
    }

    public string GetCollectorToken()
    {
        if (string.IsNullOrWhiteSpace(CollectorToken)) return string.Empty;
        if (!CollectorToken.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return CollectorToken; // One-read compatibility before Migrate saves v9.
        try
        {
            var encrypted = Convert.FromBase64String(CollectorToken[ProtectedPrefix.Length..]);
            var clear = ProtectedData.Unprotect(encrypted, TokenEntropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    public void SetCollectorToken(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            CollectorToken = string.Empty;
            return;
        }
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(normalized), TokenEntropy,
            DataProtectionScope.CurrentUser);
        CollectorToken = ProtectedPrefix + Convert.ToBase64String(encrypted);
    }
}
