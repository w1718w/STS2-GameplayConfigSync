# Gameplay Config Sync — evidence and development notes

## What was inspected

This implementation is based on concrete source and installed-binary inspection, not invented API names.

| Material | How it was used | Confidence |
| --- | --- | --- |
| Installed game 0.111.0 managed assemblies | Verified multiplayer types, method signatures, message registration, lobby/run lifecycle, manifests, and logging | Exact for the installed build; may change in later game builds |
| Installed BaseLib 3.4.7 DLL plus its public GitHub source | Verified `ModConfigRegistry.GetAll()`, public config properties, `ICustomMessage`, `CustomMessageWrapper.Send`, and quit-time config saving | Public framework API/source |
| Installed RitsuLib 0.6.2 DLL/XML/PDB plus its public repository | Verified registry/page/binding shapes and identified the missing public temporary-write API | Public discovery API plus one explicitly isolated private compatibility adapter |
| Installed AutoModSubscriber 0.1.4, manifest, decompiled DLL, and public source | Verified the `PeerVersionInfo.otherMods` sidecar, stale-state clearing, and one-sided compatibility behavior | Public source cross-checked against the actually installed binary |
| Installed Multiplayer Mod Sync 0.3.3 and Load Order Manager 0.3.0 | Verified the dependency chain, host order/hash sidecar, mismatch UI interception, SHA-256 runtime-file checks, pending restart verification, settings writes, and logging approach | Installed binaries/manifests; Multiplayer Mod Sync source was not found publicly during this review |
| Real `godot.log` startup runs | Verified DLL load, manifest version, initialization, and structured GCS events in the actual game process | Runtime evidence for startup only |

The mod also writes the same sanitized events to `user://GameplayConfigSync/logs/gameplay-config-sync.log`, rotated at 2 MiB with five archives. It never logs raw configuration values.

## APIs: real versus compatibility assumptions

Real/public dependencies used directly:

- `ModManager.GetLoadedMods()` and each manifest's `affectsGameplay` flag.
- BaseLib `ModConfigRegistry.GetAll()`, `ModConfig.ModId`, `Changed()`, `ConfigReloaded()`, `ICustomMessage`, and `CustomMessageWrapper.Send()`.
- Game `INetGameService.RegisterMessageHandler<T>()` / `UnregisterMessageHandler<T>()` and host/client identity/type data.
- Public static config properties, excluding `[ConfigIgnore]`.

Version-sensitive integration points:

- Harmony patches on `PeerVersionInfo.LocalDefault`, `HandshakeManager.TryReadHandshakeMessage`, lobby methods, run initialization/cleanup, and `NGame.Quit`.
- RitsuLib discovery is optional reflection so the mod still loads without RitsuLib.
- The RitsuLib 0.6.2 autosave wrapper's compiler-generated `<inner>P` field is the only private member used. Failure to find it disables that adapter instead of guessing.

The sidecar contains only protocol/version capability data. Configuration values are not placed in the handshake, logs, or manifest. The larger snapshot travels only after both peers have advertised a compatible protocol.

## Why the community mods matter

AutoModSubscriber demonstrates a safe asymmetric pattern: an unmodded client can ignore a host's extra non-gameplay `otherMods` entry, while a modded client removes and interprets it. If the host has no sidecar, its client-only fallback uses the game's existing mismatch list; it does not magically read host config.

Multiplayer Mod Sync builds on that mechanism. Its installed 0.3.3 payload sends ordered loaded-mod metadata and content hashes in a handshake sidecar, then uses AutoModSubscriber for Workshop installation and Load Order Manager for next-launch settings. Its Workshop instructions say both peers should install it. This supports our conclusion that automatic configuration transfer also needs both peers, even though one-sided installation can remain harmless.

GameplayConfigSync copies only the capability-discovery idea. It does not download mods, alter load order, write `settings.save`, or restart the game.

## Development and test flow

1. Inspect the target game build and dependency manifests/assemblies.
2. Confirm API names against public source where available; isolate reflection behind fail-closed adapters.
3. Compile against the exact installed managed assemblies and BaseLib DLL.
4. Install DLL + manifest as a local mod.
5. Launch the real game and verify initialization/log events.
6. Run a two-machine/two-account matrix: both installed, host only, client only, protocol mismatch, disconnect, quit, hard crash, load-run/late join, and multiple clients.
7. For actual config behavior, test both `DryRun=true` and apply mode with intentionally different gameplay settings. Correlate host/client logs by request ID and SHA-256 prefix.

Compilation and main-menu loading are necessary but do not prove multiplayer synchronization. The remaining acceptance gate is a real two-peer session.

## Crash behavior

Overrides are kept in process memory. A hard process crash discards them; on the next launch, BaseLib/RitsuLib normally reload the original on-disk values. On normal quit, the Priority.First patch restores originals before BaseLib's quit patch saves every config.

There is still a bounded third-party risk: a config property setter or reload callback may itself write to disk or perform irreversible side effects. The mod cannot generically undo arbitrary code in another mod. Ritsu bindings whose persistence behavior is unknown are therefore skipped by default, and two-peer testing must include the actual target mods before calling the system stable.
