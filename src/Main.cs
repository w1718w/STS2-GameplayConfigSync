using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaseLib;
using BaseLib.Abstracts;
using BaseLib.Config;
using GodotOS = Godot.OS;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace GameplayConfigSync;

[ModInitializer(nameof(Initialize))]
public static class Main
{
    public const string ModId = "GameplayConfigSync";
    public const string Version = "0.3.0";
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        ModConfigRegistry.Register(ModId, new SyncModConfig());
        new Harmony("wangao.sts2.gameplay-config-sync").PatchAll(Assembly.GetExecutingAssembly());
        SyncLog.Info("INIT_OK", $"version={Version} protocol={ConfigSyncSession.ProtocolVersion} " +
            $"dryRun={SyncModConfig.DryRun} ritsuUnsafe={SyncModConfig.AllowUnsafeRitsuBindings}");
    }
}

public enum SyncDiagnostics
{
    Normal,
    Verbose
}

public sealed class SyncModConfig : SimpleModConfig
{
    public static bool Enabled { get; set; } = true;
    public static bool DryRun { get; set; }
    public static SyncDiagnostics Diagnostics { get; set; } = SyncDiagnostics.Normal;
    public static bool FileLogging { get; set; } = true;
    public static bool AllowUnsafeRitsuBindings { get; set; }
}

internal static class SyncLog
{
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Logger = new(Main.ModId, LogType.Generic);
    private static readonly object FileLock = new();
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int ArchiveCount = 5;
    private static long _sequence;

    public static void Info(string code, string message) => Write("INFO", code, message);

    public static void Verbose(string code, string message)
    {
        if (SyncModConfig.Diagnostics == SyncDiagnostics.Verbose)
            Write("DEBUG", code, message);
    }

    public static void Warn(string code, string message) => Write("WARN", code, message);

    public static void Error(string code, Exception exception) => Write("ERROR", code, exception.ToString());

    private static void Write(string level, string code, string message)
    {
        string line = $"GCS|utc={DateTime.UtcNow:O}|seq={System.Threading.Interlocked.Increment(ref _sequence)}|" +
            $"level={level}|event={code}|{message}";
        if (level == "ERROR") Logger.Error(line);
        else if (level == "WARN") Logger.Warn(line);
        else Logger.Info(line);

        if (!SyncModConfig.FileLogging)
            return;
        try
        {
            lock (FileLock)
            {
                string directory = Path.Combine(GodotOS.GetUserDataDir(), Main.ModId, "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "gameplay-config-sync.log");
                RotateIfNeeded(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never interrupt a game or a network handler.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxLogBytes)
            return;
        string oldest = path + "." + ArchiveCount;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = ArchiveCount - 1; i >= 1; i--)
        {
            string source = path + "." + i;
            if (File.Exists(source)) File.Move(source, path + "." + (i + 1), true);
        }
        File.Move(path, path + ".1", true);
    }
}

internal sealed class ConfigEntry
{
    public string Source { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
}

internal sealed class ConfigSnapshot
{
    public int Version { get; set; } = 1;
    public List<ConfigEntry> Entries { get; set; } = new();
}

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

internal static class BaseLibConfigBridge
{
    private const string Source = "BaseLib";

    public static IEnumerable<ConfigEntry> Capture(IReadOnlySet<string> gameplayMods)
    {
        foreach (ModConfig config in ModConfigRegistry.GetAll())
        {
            string? modId = config.ModId;
            if (string.IsNullOrWhiteSpace(modId) || !gameplayMods.Contains(modId))
                continue;

            foreach (PropertyInfo property in GetProperties(config))
            {
                ConfigEntry? entry = TryCapture(config, property);
                if (entry is not null)
                    yield return entry;
            }
        }
    }

    public static int Apply(IReadOnlyDictionary<string, ConfigEntry> incoming, IDictionary<string, Action> restoreActions)
    {
        int changed = 0;
        foreach (ModConfig config in ModConfigRegistry.GetAll())
        {
            string? modId = config.ModId;
            if (string.IsNullOrWhiteSpace(modId))
                continue;
            bool configChanged = false;
            foreach (PropertyInfo property in GetProperties(config))
            {
                var probe = new ConfigEntry { Source = Source, ModId = modId, Key = property.Name };
                string id = ConfigSyncSession.EntryId(probe);
                if (!incoming.TryGetValue(id, out ConfigEntry? entry))
                    continue;

                try
                {
                    object? local = property.GetValue(null);
                    string localJson = ConfigSyncSession.SerializeValue(local, property.PropertyType);
                    if (string.Equals(localJson, entry.Json, StringComparison.Ordinal))
                        continue;

                    object? original = local;
                    restoreActions.TryAdd(id, () =>
                    {
                        property.SetValue(null, original);
                        config.Changed();
                        config.ConfigReloaded();
                    });
                    property.SetValue(null, ConfigSyncSession.DeserializeValue(entry.Json, property.PropertyType));
                    changed++;
                    configChanged = true;
                    SyncLog.Verbose("BASELIB_APPLY", $"mod={modId} key={property.Name}");
                }
                catch (Exception ex)
                {
                    SyncLog.Warn("BASELIB_SKIP", $"mod={config.ModId} key={property.Name} error={ex.GetType().Name}:{ex.Message}");
                }
            }

            if (configChanged)
            {
                config.Changed();
                config.ConfigReloaded();
            }
        }
        return changed;
    }

    private static IEnumerable<PropertyInfo> GetProperties(ModConfig config)
    {
        return config.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                p.GetMethod?.IsStatic == true && p.GetCustomAttribute<ConfigIgnoreAttribute>() is null);
    }

    private static ConfigEntry? TryCapture(ModConfig config, PropertyInfo property)
    {
        try
        {
            string? modId = config.ModId;
            if (string.IsNullOrWhiteSpace(modId))
                return null;
            return new ConfigEntry
            {
                Source = Source,
                ModId = modId,
                Key = property.Name,
                Json = ConfigSyncSession.SerializeValue(property.GetValue(null), property.PropertyType)
            };
        }
        catch (Exception ex)
        {
            SyncLog.Warn("BASELIB_CAPTURE_SKIP", $"mod={config.ModId} key={property.Name} error={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }
}

internal static class RitsuConfigBridge
{
    private const string Source = "RitsuLib";
    private const string RegistryTypeName = "STS2RitsuLib.Settings.ModSettingsRegistry, STS2-RitsuLib.Settings";
    private const string ValueBindingInterfaceName = "STS2RitsuLib.Settings.IModSettingsValueBinding`1";

    public static IEnumerable<ConfigEntry> Capture(IReadOnlySet<string> gameplayMods)
    {
        foreach (BindingView view in DiscoverBindings())
        {
            if (!gameplayMods.Contains(view.ModId))
                continue;
            ConfigEntry? entry = TryCapture(view);
            if (entry is not null)
                yield return entry;
        }
    }

    public static int Apply(IReadOnlyDictionary<string, ConfigEntry> incoming, IDictionary<string, Action> restoreActions)
    {
        int changed = 0;
        foreach (BindingView view in DiscoverBindings())
        {
            var probe = new ConfigEntry { Source = Source, ModId = view.ModId, Key = view.DataKey };
            string id = ConfigSyncSession.EntryId(probe);
            if (!incoming.TryGetValue(id, out ConfigEntry? entry))
                continue;

            try
            {
                object? local = view.Read();
                string localJson = ConfigSyncSession.SerializeValue(local, view.ValueType);
                if (string.Equals(localJson, entry.Json, StringComparison.Ordinal))
                    continue;

                object? original = local;
                restoreActions.TryAdd(id, () => view.Write(original));
                view.Write(ConfigSyncSession.DeserializeValue(entry.Json, view.ValueType));
                changed++;
                SyncLog.Verbose("RITSULIB_APPLY", $"mod={view.ModId} key={view.DataKey} mode={view.SafetyMode}");
            }
            catch (Exception ex)
            {
                SyncLog.Warn("RITSULIB_SKIP", $"mod={view.ModId} key={view.DataKey} error={ex.GetType().Name}:{ex.Message}");
            }
        }
        return changed;
    }

    private static ConfigEntry? TryCapture(BindingView view)
    {
        try
        {
            return new ConfigEntry
            {
                Source = Source,
                ModId = view.ModId,
                Key = view.DataKey,
                Json = ConfigSyncSession.SerializeValue(view.Read(), view.ValueType)
            };
        }
        catch (Exception ex)
        {
            SyncLog.Warn("RITSULIB_CAPTURE_SKIP", $"mod={view.ModId} key={view.DataKey} error={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    private static IEnumerable<BindingView> DiscoverBindings()
    {
        Type? registry = Type.GetType(RegistryTypeName, throwOnError: false);
        MethodInfo? getPages = registry?.GetMethod("GetPages", BindingFlags.Public | BindingFlags.Static);
        if (getPages?.Invoke(null, null) is not IEnumerable pages)
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (object page in pages)
        {
            if (GetProperty(page, "Sections") is not IEnumerable sections)
                continue;
            foreach (object section in sections)
            {
                if (GetProperty(section, "Entries") is not IEnumerable entries)
                    continue;
                foreach (object entry in entries)
                {
                    object leaf = UnwrapEntry(entry);
                    object? binding = GetProperty(leaf, "Binding");
                    BindingView? view = BindingView.TryCreate(binding);
                    if (view is not null && seen.Add(view.ModId + "\0" + view.DataKey))
                        yield return view;
                }
            }
        }
    }

    private static object UnwrapEntry(object entry)
    {
        object current = entry;
        while (current.GetType().FullName == "STS2RitsuLib.Settings.ModSettingsEntryDecorator")
        {
            object? inner = GetProperty(current, "Inner");
            if (inner is null)
                break;
            current = inner;
        }
        return current;
    }

    private static object? GetProperty(object value, string name) => value.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
        ?.GetValue(value);

    private sealed class BindingView
    {
        private readonly object _binding;
        private readonly MethodInfo _read;
        private readonly MethodInfo _write;

        private BindingView(object binding, Type bindingInterface, string modId, string dataKey, string safetyMode)
        {
            _binding = binding;
            Type effectiveInterface = FindValueInterface(_binding.GetType()) ?? bindingInterface;
            _read = effectiveInterface.GetMethod("Read")!;
            _write = effectiveInterface.GetMethod("Write")!;
            ValueType = effectiveInterface.GetGenericArguments()[0];
            ModId = modId;
            DataKey = dataKey;
            SafetyMode = safetyMode;
        }

        public string ModId { get; }
        public string DataKey { get; }
        public string SafetyMode { get; }
        public Type ValueType { get; }
        public object? Read() => _read.Invoke(_binding, null);
        public void Write(object? value) => _write.Invoke(_binding, new[] { value });

        public static BindingView? TryCreate(object? binding)
        {
            if (binding is null)
                return null;
            Type? valueInterface = FindValueInterface(binding.GetType());
            if (valueInterface is null)
                return null;
            string? modId = binding.GetType().GetProperty("ModId")?.GetValue(binding) as string;
            string? dataKey = binding.GetType().GetProperty("DataKey")?.GetValue(binding) as string;
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(dataKey))
                return null;

            bool transient = binding.GetType().GetInterfaces().Any(i =>
                i.FullName == "STS2RitsuLib.Settings.ITransientModSettingsBinding");
            if (transient)
                return new BindingView(binding, valueInterface, modId, dataKey, "transient-public");

            object? unwrapped = TryUnwrapAutoSave(binding);
            if (unwrapped is not null)
                return new BindingView(unwrapped, valueInterface, modId, dataKey, "autosave-compat");

            if (SyncModConfig.AllowUnsafeRitsuBindings)
                return new BindingView(binding, valueInterface, modId, dataKey, "unsafe-opt-in");

            SyncLog.Verbose("RITSULIB_BINDING_SKIP", $"mod={modId} key={dataKey} reason=persistence_unknown");
            return null;
        }

        private static Type? FindValueInterface(Type type) => type.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition().FullName == ValueBindingInterfaceName);

        private static object? TryUnwrapAutoSave(object binding)
        {
            object current = binding;
            bool unwrapped = false;
            while (current.GetType().FullName?.StartsWith(
                       "STS2RitsuLib.Settings.AutoSaveModSettingsValueBinding`1",
                       StringComparison.Ordinal) == true)
            {
                FieldInfo? inner = current.GetType().GetField("<inner>P", BindingFlags.Instance | BindingFlags.NonPublic);
                object? next = inner?.GetValue(current);
                if (next is null)
                {
                    SyncLog.Warn("RITSULIB_COMPAT_DISABLED", "reason=autosave_inner_missing");
                    return null;
                }
                current = next;
                unwrapped = true;
            }
            return unwrapped ? current : null;
        }
    }
}

public sealed class ConfigRequestMessage : ICustomMessage
{
    public int Protocol;
    public string RequestId = "";
    public string ClientVersion = "";
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteString(RequestId);
        writer.WriteString(ClientVersion);
    }
    public void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        RequestId = reader.ReadString();
        ClientVersion = reader.ReadString();
    }
    public void HandleMessage(ulong senderId) =>
        ConfigSyncSession.HandleRequest(senderId, Protocol, RequestId, ClientVersion);
}

public sealed class ConfigSnapshotMessage : ICustomMessage
{
    public int Protocol;
    public string RequestId = "";
    public string HostVersion = "";
    public string PayloadHash = "";
    public string Payload = "";
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteString(RequestId);
        writer.WriteString(HostVersion);
        writer.WriteString(PayloadHash);
        writer.WriteString(Payload);
    }
    public void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        RequestId = reader.ReadString();
        HostVersion = reader.ReadString();
        PayloadHash = reader.ReadString();
        Payload = reader.ReadString();
    }
    public void HandleMessage(ulong senderId) => ConfigSyncSession.HandleSnapshot(
        senderId, Protocol, RequestId, HostVersion, PayloadHash, Payload);
}

[HarmonyPatch(typeof(PeerVersionInfo), "LocalDefault")]
internal static class LocalPeerCapabilityPatch
{
    private static void Postfix(ref PeerVersionInfo __result) => PeerCapability.Attach(ref __result);
}

[HarmonyPatch(typeof(HandshakeManager), "TryReadHandshakeMessage")]
internal static class RemotePeerCapabilityPatch
{
    private static void Postfix(ref HandshakeResult __result)
    {
        if (__result.remoteVersionInfo.HasValue)
            PeerCapability.Extract(__result.remoteVersionInfo.Value.otherMods);
        else
            PeerCapability.Extract(null);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.AddLocalHostPlayer))]
internal static class StartLobbyHostPatch
{
    private static void Postfix(StartRunLobby __instance) => ConfigSyncSession.Attach(__instance.NetService, false);
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.InitializeFromMessage))]
internal static class StartLobbyClientPatch
{
    private static void Postfix(StartRunLobby __instance) => ConfigSyncSession.Attach(__instance.NetService, true);
}

[HarmonyPatch]
internal static class LoadLobbyConstructorPatch
{
    // LoadRunLobby 有两个构造函数，其中接收 ClientLoadJoinResponseMessage 的那个会链式
    // 调用另一个。此处把该类型的全部构造函数都挂上了补丁，因此「客户端加入已保存的对局」
    // 时，一次对象创建会触发两次 Postfix（同一实例、相隔不到一毫秒）。
    // 结果是重复的 SESSION_ATTACH，以及双端时重复的请求。
    //
    // 一个实例的构造过程只会发生一次，所以按实例去重即可精确消掉这次重复，不会误伤
    // 其它合法的重复挂载路径。用弱引用持有，避免延长该大厅对象的生命周期。
    private static WeakReference<LoadRunLobby>? _lastConstructed;

    private static IEnumerable<MethodBase> TargetMethods() => typeof(LoadRunLobby).GetConstructors();

    private static void Postfix(LoadRunLobby __instance)
    {
        if (_lastConstructed is not null &&
            _lastConstructed.TryGetTarget(out LoadRunLobby? previous) &&
            ReferenceEquals(previous, __instance))
        {
            SyncLog.Verbose("ATTACH_SKIPPED", "reason=duplicate_constructor");
            return;
        }

        _lastConstructed = new WeakReference<LoadRunLobby>(__instance);
        ConfigSyncSession.Attach(__instance.NetService, __instance.NetService.Type == NetGameType.Client);
    }
}

[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally")]
internal static class StartLobbyBeginPatch
{
    private static void Prefix() => ConfigSyncSession.DetachHandler();
}

[HarmonyPatch(typeof(LoadRunLobby), "BeginRunLocally")]
internal static class LoadLobbyBeginPatch
{
    private static void Prefix() => ConfigSyncSession.DetachHandler();
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
internal static class StartLobbyCleanupPatch
{
    private static void Prefix(bool disconnectSession)
    {
        if (disconnectSession) ConfigSyncSession.RestoreAndDetach();
        else ConfigSyncSession.DetachHandler();
    }
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.CleanUp))]
internal static class LoadLobbyCleanupPatch
{
    private static void Prefix(bool disconnectSession)
    {
        if (disconnectSession) ConfigSyncSession.RestoreAndDetach();
        else ConfigSyncSession.DetachHandler();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class RunCleanupPatch
{
    private static void Postfix() => ConfigSyncSession.RestoreAndDetach();
}

[HarmonyPatch(typeof(NGame), nameof(NGame.Quit))]
internal static class GameQuitPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix() => ConfigSyncSession.RestoreAndDetach();
}

[HarmonyPatch(typeof(RunManager), "InitializeShared")]
internal static class RunInitializePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(INetGameService netService) => ConfigSyncSession.ContinueInRun(netService);
}
