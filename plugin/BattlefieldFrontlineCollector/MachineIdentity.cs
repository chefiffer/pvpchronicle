using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace BattlefieldFrontlineCollector;

/// <summary>
/// Produces a one-way, plugin-specific machine identifier. The raw Windows
/// MachineGuid is never transmitted or persisted by the plugin.
/// 生成插件专用的单向机器标识；不会上传或保存原始 MachineGuid。
/// </summary>
internal static class MachineIdentity
{
    public static string GetStableId(Configuration configuration)
    {
        var source = ReadWindowsMachineGuid();
        if (string.IsNullOrWhiteSpace(source)) source = configuration.MachineFallbackId;
        var material = Encoding.UTF8.GetBytes(
            $"BattlefieldFrontlineCollector.Machine.v1:{source}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    public static string? GetGameAccountId(string playerName, ushort homeWorldId)
    {
        if (string.IsNullOrWhiteSpace(playerName) || homeWorldId == 0) return null;
        var material = Encoding.UTF8.GetBytes(
            $"BattlefieldFrontlineCollector.GameAccount.v1:{playerName.Trim()}@{homeWorldId}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static string? ReadWindowsMachineGuid()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
            System.IO.IOException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
