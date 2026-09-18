using System.Collections.Generic;

namespace GameplayConfigSync;

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
