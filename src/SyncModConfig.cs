using System.Collections.Generic;
using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace GameplayConfigSync;

public enum SyncDiagnostics
{
    Normal,
    Verbose
}

[ConfigHoverTipsByDefault]
public sealed class SyncModConfig : SimpleModConfig
{
    public static bool Enabled { get; set; } = true;
    public static bool DryRun { get; set; }
    public static SyncDiagnostics Diagnostics { get; set; } = SyncDiagnostics.Normal;
    public static bool FileLogging { get; set; } = true;
    public static bool AllowUnsafeRitsuBindings { get; set; }
}

internal static class ConfigLocalization
{
    private static readonly Dictionary<string, string> English = new()
    {
        ["GAMEPLAYCONFIGSYNC.mod_title"] = "Gameplay Config Sync",
        ["GAMEPLAYCONFIGSYNC-ENABLED.title"] = "Enabled",
        ["GAMEPLAYCONFIGSYNC-ENABLED.hover.desc"] = "Enable multiplayer gameplay configuration synchronization.",
        ["GAMEPLAYCONFIGSYNC-DRY_RUN.title"] = "Dry Run",
        ["GAMEPLAYCONFIGSYNC-DRY_RUN.hover.desc"] = "Validate and log snapshots without applying their values.",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.title"] = "Diagnostics",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.hover.desc"] = "Choose the amount of diagnostic information written to logs.",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.Normal"] = "Normal",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.Verbose"] = "Verbose",
        ["GAMEPLAYCONFIGSYNC-FILE_LOGGING.title"] = "File Logging",
        ["GAMEPLAYCONFIGSYNC-FILE_LOGGING.hover.desc"] = "Write Gameplay Config Sync events to a dedicated log file.",
        ["GAMEPLAYCONFIGSYNC-ALLOW_UNSAFE_RITSU_BINDINGS.title"] = "Allow Unsafe RitsuLib Bindings",
        ["GAMEPLAYCONFIGSYNC-ALLOW_UNSAFE_RITSU_BINDINGS.hover.desc"] = "Allow RitsuLib settings whose persistence behavior cannot be proven safe. Disabled by default."
    };

    private static readonly Dictionary<string, string> SimplifiedChinese = new()
    {
        ["GAMEPLAYCONFIGSYNC.mod_title"] = "联机玩法配置同步",
        ["GAMEPLAYCONFIGSYNC-ENABLED.title"] = "启用",
        ["GAMEPLAYCONFIGSYNC-ENABLED.hover.desc"] = "启用联机玩法配置同步。",
        ["GAMEPLAYCONFIGSYNC-DRY_RUN.title"] = "仅测试，不应用",
        ["GAMEPLAYCONFIGSYNC-DRY_RUN.hover.desc"] = "只校验并记录主机快照，不把配置值应用到本机。",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.title"] = "诊断日志",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.hover.desc"] = "选择写入日志的诊断信息详细程度。",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.Normal"] = "普通",
        ["GAMEPLAYCONFIGSYNC-DIAGNOSTICS.Verbose"] = "详细",
        ["GAMEPLAYCONFIGSYNC-FILE_LOGGING.title"] = "独立文件日志",
        ["GAMEPLAYCONFIGSYNC-FILE_LOGGING.hover.desc"] = "把联机玩法配置同步事件写入独立日志文件。",
        ["GAMEPLAYCONFIGSYNC-ALLOW_UNSAFE_RITSU_BINDINGS.title"] = "允许不安全的 RitsuLib 绑定",
        ["GAMEPLAYCONFIGSYNC-ALLOW_UNSAFE_RITSU_BINDINGS.hover.desc"] = "允许同步无法确认持久化行为是否安全的 RitsuLib 设置；默认关闭。"
    };

    public static void Apply(LocManager locManager)
    {
        try
        {
            Dictionary<string, string> entries = locManager.Language == "zhs"
                ? SimplifiedChinese
                : English;
            locManager.GetTable("settings_ui").MergeWith(entries);
        }
        catch (KeyNotFoundException)
        {
            // SetLanguage can run before settings_ui is loaded; Initialize's postfix retries afterwards.
        }
    }
}

[HarmonyPatch(typeof(LocManager), nameof(LocManager.Initialize))]
internal static class ConfigLocalizationInitializePatch
{
    private static void Postfix() => ConfigLocalization.Apply(LocManager.Instance);
}

[HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
internal static class ConfigLocalizationLanguagePatch
{
    private static void Postfix(LocManager __instance) => ConfigLocalization.Apply(__instance);
}
