using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BaseLib.Config;

namespace GameplayConfigSync;

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
