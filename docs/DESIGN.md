# Gameplay Config Sync — multiplayer hardening record

## Safety contract

- Synchronization happens only when the client observes a host capability marker with the same protocol version.
- A one-sided installation is inert: the host never pushes snapshots, and the client never sends a request without a compatible host marker.
- The host is authoritative only for settings registered by loaded mods whose manifest has `affects_gameplay=true`.
- GameplayConfigSync's own settings are always excluded.
- A snapshot is accepted only from `NetClientGameService.HostNetId`, for the client's current request ID, with the current protocol version and a matching SHA-256 prefix.
- Unknown or persistence-unsafe adapters fail closed. A skipped setting is safer than a guessed write.
- Original client values are retained in memory and restored on lobby cleanup, run cleanup, disconnect, and the normal game quit path.

## Compatibility boundaries

| Area | Mechanism | Stability |
| --- | --- | --- |
| BaseLib registry | `ModConfigRegistry.GetAll()` | Public API |
| BaseLib settings | Public static properties excluding `[ConfigIgnore]` | Public reflection contract used by BaseLib itself |
| Multiplayer carrier | `ICustomMessage`, `CustomMessageWrapper.Send` | Public BaseLib API |
| Capability discovery | `PeerVersionInfo.LocalDefault`, `HandshakeManager.TryReadHandshakeMessage`, `otherMods` sidecar | Game types/methods, community-proven Harmony integration; version-sensitive |
| Lobby handler | `INetGameService.RegisterMessageHandler<T>` | Public game API |
| Run handler | BaseLib's `RunManager.InitializeShared` patch | Public BaseLib behavior |
| Ritsu registry/value binding | `GetPages`, `IModSettingsValueBinding<T>` via optional reflection | Public names, optional runtime dependency |
| Ritsu autosave bypass | compiler-generated `<inner>P` on 0.6.2 wrapper | Private compatibility adapter; probed and skipped on mismatch |

No BaseLib private message dictionary, private handler method, or private `ConfigProperties` field is accessed.

## Structured log protocol

Every diagnostic line contains `GCS|time=<local ISO-8601 with offset>|seq=<n>|level=<level>|event=<code>|...`. It goes to both the game log and, by default, a dedicated log file for each game launch. Important codes:

- `INIT_OK`
- `CAPABILITY_ATTACH`, `CAPABILITY_OK`, `CAPABILITY_MISSING`, `CAPABILITY_INVALID`, `CAPABILITY_INCOMPATIBLE`
- `SESSION_ATTACH`, `RUN_ATTACH`
- `REQUEST_SEND`, `REQUEST_SKIPPED`, `REQUEST_REJECT`
- `SNAPSHOT_CAPTURE`, `SNAPSHOT_SEND`, `SNAPSHOT_REJECT`
- `SNAPSHOT_APPLY_OK`, `SNAPSHOT_DRY_RUN`, `SNAPSHOT_APPLY_FAILED`
- `BASELIB_APPLY`, `RITSULIB_APPLY` in verbose mode
- `BASELIB_SKIP`, `RITSULIB_SKIP`, `RITSULIB_COMPAT_DISABLED`
- `RESTORE_OK`, `RESTORE_ENTRY_FAILED`

Raw configuration values are not logged. Match host and client by `request=` and compare `sha256=`.

## Required two-peer acceptance test

1. Install identical game, BaseLib, GameplayConfigSync, and gameplay-mod versions on host and client.
2. On the client enable `DryRun` and `Diagnostics=Verbose`; use deliberately different gameplay settings.
3. Join and start a disposable multiplayer run.
4. Confirm the request, capture, send, and dry-run events correlate and hashes match.
5. Disable `DryRun`, reconnect, and confirm `SNAPSHOT_APPLY_OK changed=>0`.
6. Leave normally and confirm `RESTORE_OK`; verify the client's settings UI and config files still contain the original values.
7. Repeat with disconnect, reconnect, late join/load-run, and normal Quit.
8. Repeat with one deliberately unserializable setting and confirm only that entry is skipped without disconnecting.
9. Repeat with the mod removed from only the host, then only the client. Confirm normal join behavior and no `REQUEST_SEND`/snapshot events.
10. Repeat with a deliberately different GameplayConfigSync protocol marker and confirm `CAPABILITY_INCOMPATIBLE` plus `REQUEST_SKIPPED`.

## Known gaps before a stable 1.0

- Capability discovery is a Harmony sidecar pattern rather than a documented stable game API. It is fail-closed and is covered by startup/handshake logs, but must be revalidated after game multiplayer changes.
- RitsuLib has no general public temporary-write-without-persistence API. Unknown bindings are skipped by default; the 0.6.2 autosave adapter remains version-sensitive.
- Apply is entry-isolated, not a fully transactional all-or-nothing commit across unrelated third-party setters.
- There is no persistent crash-recovery journal. A hard process crash clears in-memory overrides, but a third-party setter that writes immediately could still persist a host value.
- Host migration, live mid-run setting changes, malicious-host semantic range validation, and localization/UI status indicators are not implemented.
- Real two-peer and multi-client tests are still required; successful compilation and main-menu loading do not prove network behavior.
