using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameplayConfigSync;

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
