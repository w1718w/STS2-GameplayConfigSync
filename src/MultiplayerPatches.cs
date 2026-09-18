using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace GameplayConfigSync;

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
