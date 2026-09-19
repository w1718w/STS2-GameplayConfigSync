using System.Reflection;
using BaseLib;
using BaseLib.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace GameplayConfigSync;

[ModInitializer(nameof(Initialize))]
public static class Main
{
    public const string ModId = "GameplayConfigSync";
    public const string Version = "0.4.0-beta.2";
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
