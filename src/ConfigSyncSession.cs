using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace GameplayConfigSync;

internal static class ConfigSyncSession
{
    internal const int ProtocolVersion = 2;
    private const int MaxEntries = 4096;
    private const int MaxPayloadBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private static readonly Dictionary<string, Action> RestoreActions = new(StringComparer.Ordinal);
    private static INetGameService? _service;
    private static bool _handlerRegistered;
    private static string? _pendingRequestId;
    private static string _sessionId = "none";
    private static readonly Dictionary<ulong, long> LastRequestTicks = new();

    public static void Attach(INetGameService service, bool requestSnapshot)
    {
        if (!SyncModConfig.Enabled)
        {
            SyncLog.Info("ATTACH_SKIPPED", "reason=disabled");
            return;
        }

        if (!ReferenceEquals(_service, service))
        {
            DetachHandler();
            _service = service;
            _sessionId = Guid.NewGuid().ToString("N")[..12];
        }

        RegisterHandler();
        SyncLog.Info("SESSION_ATTACH", $"session={_sessionId} role={service.Type} connected={service.IsConnected}");

        if (requestSnapshot && service.Type == NetGameType.Client && service.IsConnected)
            RequestIfSupported(service, "lobby");
    }

    public static void DetachHandler()
    {
        if (_handlerRegistered && _service is not null)
        {
            try
            {
                _service.UnregisterMessageHandler<CustomMessageWrapper>(HandleWrappedMessage);
            }
            catch (Exception ex)
            {
                SyncLog.Error("HANDLER_UNREGISTER_FAILED", ex);
            }
        }

        _handlerRegistered = false;
        _service = null;
        _pendingRequestId = null;
    }

    public static void RestoreAndDetach()
    {
        DetachHandler();
        RestoreLocalValues();
        PeerCapability.Reset();
    }

    public static void ContinueInRun(INetGameService service)
    {
        if (!SyncModConfig.Enabled)
            return;
        _service = service;
        _handlerRegistered = false; // BaseLib owns the wrapper handler after RunManager.InitializeShared.
        SyncLog.Info("RUN_ATTACH", $"session={_sessionId} role={service.Type} connected={service.IsConnected}");
        if (service.Type == NetGameType.Client && service.IsConnected)
            RequestIfSupported(service, "run");
    }

    public static void HandleRequest(ulong senderId, int protocolVersion, string requestId, string clientVersion)
    {
        if (_service is null || _service.Type != NetGameType.Host || !_service.IsConnected)
            return;
        if (protocolVersion != ProtocolVersion || !IsValidRequestId(requestId))
        {
            SyncLog.Warn("REQUEST_REJECT", $"session={_sessionId} sender={senderId} protocol={protocolVersion} request={requestId}");
            return;
        }
        long now = Environment.TickCount64;
        if (LastRequestTicks.TryGetValue(senderId, out long previous) && now - previous < 1000)
        {
            SyncLog.Warn("REQUEST_REJECT", $"session={_sessionId} sender={senderId} request={requestId} reason=rate_limit");
            return;
        }
        LastRequestTicks[senderId] = now;

        ConfigSnapshot snapshot = CaptureHostSnapshot();
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        int payloadBytes = Encoding.UTF8.GetByteCount(json);
        if (payloadBytes > MaxPayloadBytes)
        {
            SyncLog.Warn("SNAPSHOT_REJECT", $"session={_sessionId} request={requestId} reason=too_large bytes={payloadBytes}");
            return;
        }

        string hash = Hash(json);
        SyncLog.Info("SNAPSHOT_SEND", $"session={_sessionId} request={requestId} sender={senderId} " +
            $"clientVersion={clientVersion} entries={snapshot.Entries.Count} bytes={payloadBytes} sha256={hash}");
        CustomMessageWrapper.Send(new ConfigSnapshotMessage
        {
            Protocol = ProtocolVersion,
            RequestId = requestId,
            HostVersion = Main.Version,
            PayloadHash = hash,
            Payload = json
        }, _service);
    }

    public static void HandleSnapshot(ulong senderId, int protocolVersion, string requestId,
        string hostVersion, string payloadHash, string payload)
    {
        if (_service is not NetClientGameService client || senderId != client.HostNetId)
        {
            SyncLog.Warn("SNAPSHOT_REJECT", $"session={_sessionId} request={requestId} reason=not_host sender={senderId}");
            return;
        }
        if (protocolVersion != ProtocolVersion || requestId != _pendingRequestId)
        {
            SyncLog.Warn("SNAPSHOT_REJECT", $"session={_sessionId} request={requestId} reason=protocol_or_request " +
                $"protocol={protocolVersion} expected={_pendingRequestId}");
            return;
        }
        if (string.IsNullOrEmpty(payload) || Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            SyncLog.Warn("SNAPSHOT_REJECT", $"session={_sessionId} request={requestId} reason=invalid_size");
            return;
        }
        string actualHash = Hash(payload);
        if (!string.Equals(actualHash, payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            SyncLog.Warn("SNAPSHOT_REJECT", $"session={_sessionId} request={requestId} reason=hash_mismatch " +
                $"expected={payloadHash} actual={actualHash}");
            return;
        }

        try
        {
            ConfigSnapshot? snapshot = JsonSerializer.Deserialize<ConfigSnapshot>(payload, JsonOptions);
            if (snapshot is null || snapshot.Version != 1 || snapshot.Entries.Count > MaxEntries)
            {
                SyncLog.Warn("SNAPSHOT_REJECT", $"session={_sessionId} request={requestId} reason=invalid_schema");
                return;
            }

            int changed = SyncModConfig.DryRun ? 0 : ApplySnapshot(snapshot);
            SyncLog.Info(SyncModConfig.DryRun ? "SNAPSHOT_DRY_RUN" : "SNAPSHOT_APPLY_OK",
                $"session={_sessionId} request={requestId} hostVersion={hostVersion} " +
                $"entries={snapshot.Entries.Count} changed={changed} sha256={actualHash}");
            _pendingRequestId = null;
        }
        catch (Exception ex)
        {
            SyncLog.Error("SNAPSHOT_APPLY_FAILED", ex);
        }
    }

    private static ConfigSnapshot CaptureHostSnapshot()
    {
        HashSet<string> gameplayMods = GetGameplayModIds();
        var entries = new List<ConfigEntry>();
        try { entries.AddRange(BaseLibConfigBridge.Capture(gameplayMods)); }
        catch (Exception ex) { SyncLog.Error("BASELIB_CAPTURE_FAILED", ex); }
        try { entries.AddRange(RitsuConfigBridge.Capture(gameplayMods)); }
        catch (Exception ex) { SyncLog.Error("RITSULIB_CAPTURE_FAILED", ex); }
        entries.Sort((a, b) => string.CompareOrdinal(EntryId(a), EntryId(b)));
        if (entries.Count > MaxEntries)
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        SyncLog.Info("SNAPSHOT_CAPTURE", $"session={_sessionId} entries={entries.Count} " +
            $"baselib={entries.Count(e => e.Source == "BaseLib")} ritsulib={entries.Count(e => e.Source == "RitsuLib")}");
        return new ConfigSnapshot { Entries = entries };
    }

    private static int ApplySnapshot(ConfigSnapshot snapshot)
    {
        HashSet<string> gameplayMods = GetGameplayModIds();
        Dictionary<string, ConfigEntry> incoming = snapshot.Entries
            .Where(e => gameplayMods.Contains(e.ModId))
            .GroupBy(EntryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        int changed = 0;
        changed += BaseLibConfigBridge.Apply(incoming, RestoreActions);
        changed += RitsuConfigBridge.Apply(incoming, RestoreActions);
        return changed;
    }

    private static void RestoreLocalValues()
    {
        foreach (Action restore in RestoreActions.Values.Reverse().ToArray())
        {
            try { restore(); }
            catch (Exception ex) { SyncLog.Error("RESTORE_ENTRY_FAILED", ex); }
        }
        if (RestoreActions.Count > 0)
            SyncLog.Info("RESTORE_OK", $"session={_sessionId} entries={RestoreActions.Count}");
        RestoreActions.Clear();
    }

    internal static string EntryId(ConfigEntry entry) => $"{entry.Source}|{entry.ModId}|{entry.Key}";

    internal static string SerializeValue(object? value, Type type) => JsonSerializer.Serialize(value, type, JsonOptions);

    internal static object? DeserializeValue(string json, Type type) => JsonSerializer.Deserialize(json, type, JsonOptions);

    private static HashSet<string> GetGameplayModIds() => ModManager.GetLoadedMods()
        .Select(mod => mod.manifest)
        .Where(manifest => manifest is not null && manifest.affectsGameplay &&
            !string.IsNullOrWhiteSpace(manifest.id) && manifest.id != Main.ModId)
        .Select(manifest => manifest!.id!)
        .ToHashSet(StringComparer.Ordinal);

    private static void SendRequest(INetGameService service, string phase)
    {
        _pendingRequestId = Guid.NewGuid().ToString("N");
        SyncLog.Info("REQUEST_SEND", $"session={_sessionId} request={_pendingRequestId} phase={phase} protocol={ProtocolVersion}");
        CustomMessageWrapper.Send(new ConfigRequestMessage
        {
            Protocol = ProtocolVersion,
            RequestId = _pendingRequestId,
            ClientVersion = Main.Version
        }, service);
    }

    private static void RequestIfSupported(INetGameService service, string phase)
    {
        if (!PeerCapability.RemoteSupportsCurrentProtocol)
        {
            SyncLog.Info("REQUEST_SKIPPED",
                $"session={_sessionId} phase={phase} reason=peer_capability_missing_or_incompatible");
            return;
        }
        SendRequest(service, phase);
    }

    private static void RegisterHandler()
    {
        if (_handlerRegistered || _service is null)
            return;
        try
        {
            _service.RegisterMessageHandler<CustomMessageWrapper>(HandleWrappedMessage);
            _handlerRegistered = true;
        }
        catch (Exception ex)
        {
            SyncLog.Error("HANDLER_REGISTER_FAILED", ex);
        }
    }

    private static void HandleWrappedMessage(CustomMessageWrapper message, ulong senderId)
    {
        try { message.Message.HandleMessage(senderId); }
        catch (Exception ex) { SyncLog.Error("MESSAGE_HANDLER_FAILED", ex); }
    }

    private static bool IsValidRequestId(string requestId) =>
        requestId.Length == 32 && Guid.TryParseExact(requestId, "N", out _);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
}
