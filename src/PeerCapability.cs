using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;

namespace GameplayConfigSync;

internal static class PeerCapability
{
    private const string Prefix = "__gcs_cap__-1\u0001";
    private static int? _remoteProtocol;

    public static bool RemoteSupportsCurrentProtocol => _remoteProtocol == ConfigSyncSession.ProtocolVersion;

    public static void Attach(ref PeerVersionInfo versionInfo)
    {
        List<string> otherMods = versionInfo.otherMods ?? new List<string>();
        versionInfo.otherMods = otherMods;
        otherMods.RemoveAll(IsMarker);
        otherMods.Add($"{Prefix}{ConfigSyncSession.ProtocolVersion}\u0001{Main.Version}");
        SyncLog.Verbose("CAPABILITY_ATTACH",
            $"protocol={ConfigSyncSession.ProtocolVersion} version={Main.Version}");
    }

    public static void Extract(List<string>? otherMods)
    {
        _remoteProtocol = null;
        if (otherMods is null)
        {
            SyncLog.Info("CAPABILITY_MISSING", "reason=no_other_mods");
            return;
        }

        bool found = false;
        for (int i = otherMods.Count - 1; i >= 0; i--)
        {
            string entry = otherMods[i];
            if (!IsMarker(entry))
                continue;

            found = true;
            otherMods.RemoveAt(i); // Keep the marker out of normal mod-mismatch reporting.
            string[] fields = entry[Prefix.Length..].Split('\u0001');
            if (fields.Length == 2 && int.TryParse(fields[0], out int protocol) &&
                protocol > 0 && fields[1].Length is > 0 and <= 32)
            {
                _remoteProtocol = protocol;
                SyncLog.Info(protocol == ConfigSyncSession.ProtocolVersion
                        ? "CAPABILITY_OK"
                        : "CAPABILITY_INCOMPATIBLE",
                    $"remoteProtocol={protocol} remoteVersion={fields[1]} localProtocol={ConfigSyncSession.ProtocolVersion}");
                return;
            }
        }

        SyncLog.Info(found ? "CAPABILITY_INVALID" : "CAPABILITY_MISSING",
            found ? "reason=malformed" : "reason=no_marker");
    }

    public static void Reset() => _remoteProtocol = null;

    private static bool IsMarker(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
}
