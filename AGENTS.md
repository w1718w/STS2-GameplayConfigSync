# Repository guidelines

## Project overview

GameplayConfigSync is a local Slay the Spire 2 DLL mod. In multiplayer it temporarily applies the host's gameplay-affecting mod settings to clients, keeps those changes in memory, and restores the client's original values when the lobby or run ends.

- Runtime: C# / .NET 9 / Godot Mono
- Required mod dependency: BaseLib
- Optional compatibility: RitsuLib settings
- Target game version: `GameplayConfigSync.json` → `min_game_version`
- Packaging: DLL-only (`has_pck: false`)

## Repository layout

- `src/` — runtime code, protocol messages, configuration bridges, Harmony patches, and logging
- `GameplayConfigSync.json` — authoritative mod manifest and public version
- `GameplayConfigSync.csproj` — build, dependency, and packaging rules
- `Directory.Build.props` — local game assembly path and CI fallback selection
- `docs/DESIGN.md` — safety contract, compatibility boundaries, and multiplayer acceptance matrix
- `docs/RESEARCH.md` — version-specific research evidence
- `tools/GameplayConfigSync.ContractChecks/` — build-integrated IL checks for Harmony target and instance-lifecycle contracts
- `.github/workflows/` — build and release validation

Build output is intentionally nested:

```text
dist/GameplayConfigSync/
├── GameplayConfigSync.dll
└── GameplayConfigSync.json
```

Do not flatten this directory. It matches both the game's `mods/GameplayConfigSync/` layout and Steam Workshop content layout.

## Build and validation

```bash
dotnet build -c Release
```

There is currently no unit-test project or linter. A successful change requires validation proportional to its scope:

1. Run `dotnet build -c Release` with zero errors.
   This automatically runs the Harmony IL contract checker. With a real game assembly it validates every target strictly; metadata-only CI references may report `GCSH102` for omitted private members.
2. Confirm `dist/GameplayConfigSync/` contains only the mod DLL and manifest.
3. For networking, lifecycle, configuration, or Harmony changes, run the relevant two-peer scenarios from `docs/DESIGN.md`.
4. Correlate host/client events by `request=` and verify matching `sha256=` values.
5. Confirm client values are restored after normal exit and any modified disconnect/cleanup path.

Compilation and reaching the main menu do not prove multiplayer behavior is correct.

If a helper Python script is genuinely needed, use `uv` (`uv run`, `uv tool install`, or `uv pip install`). Do not introduce a Python environment for tasks already covered by the .NET toolchain.

## Architecture invariants

Preserve these rules unless the task explicitly changes the protocol or safety model:

- Synchronization starts only after the client observes a compatible host capability marker.
- One-sided installation must remain inert: no request, snapshot, or unknown custom message is sent.
- The host is authoritative only for settings belonging to loaded mods whose manifest declares `affects_gameplay=true`.
- GameplayConfigSync's own settings are never included in snapshots.
- Clients accept snapshots only from the current host, for the current request ID and protocol, with a valid payload hash and size/count limits.
- Unknown or persistence-unsafe adapters fail closed. Skipping an entry is safer than guessing how to write it.
- Original client values remain in memory and are restored on lobby cleanup, run cleanup, disconnect, and normal game quit.
- Logs must not contain raw configuration values.

Treat session lifecycle changes as high risk. A lobby can transition into a run without disconnecting, and load-run constructors may chain. Avoid duplicate handler registration, duplicate requests, lost request IDs, or skipped restoration.

## Dependency and compatibility rules

- Never package BaseLib. Its package reference must retain `PrivateAssets=all` and `ExcludeAssets=runtime`.
- Do not commit game assemblies or other redistributable-incompatible binaries.
- Prefer documented public APIs. Reflection against optional libraries must be isolated, version-checked where necessary, and fail closed.
- The local build uses real game DLLs when `STS2GameDir` exists; CI falls back to `BSchneppe.Sts2.ReferenceAssemblies`.
- When changing assembly references, verify that local and CI builds target compatible assembly identities.

When updating the supported game version, update both:

1. `GameplayConfigSync.json` → `min_game_version`
2. `GameplayConfigSync.csproj` → `BSchneppe.Sts2.ReferenceAssemblies` package version

## Code conventions

- Keep nullable reference types enabled and avoid suppressions unless the invariant is documented.
- Prefer small files organized by responsibility; do not accumulate unrelated protocol, UI, bridge, and patch code in one file.
- Keep Harmony patches thin. Put behavior in named domain classes and let patches only forward game events.
- Keep wire validation explicit and close to message handling.
- Preserve structured `GCS|...` event codes when changing log formatting; update `README.md`, `README.en.md`, and `docs/DESIGN.md` together when the documented log contract changes.
- Avoid speculative abstractions and unrelated cleanup. Every changed line should support the requested behavior.

## Versioning and documentation

The mod version exists in exactly three places and must match:

- `GameplayConfigSync.json` → `version` (authoritative)
- `GameplayConfigSync.csproj` → `<Version>` (assembly metadata)
- `src/Main.cs` → `Main.Version` (runtime capability and log metadata)

CI rejects mismatches. Do not add a hard-coded version to README prose; the badges already cover mod and game versions.

Version numbers in `docs/RESEARCH.md` and `docs/DESIGN.md` are historical evidence of what was tested. Do not update them merely because a newer dependency exists.

## Git and release workflow

- Use Conventional Commits: `feat`, `fix`, `docs`, `build`, or `refactor` as appropriate.
- `chore` and `ci` commits are intentionally omitted from generated changelog sections.
- Do not commit `dist/`, `bin/`, `obj/`, runtime logs, test-result logs, game DLLs, or BaseLib DLLs.
- The repository intentionally has no license. Do not add one unless the owner explicitly requests it.
- Preserve unrelated working-tree changes.

Release outline:

```bash
git-cliff --unreleased --prepend CHANGELOG.md
git tag vX.Y.Z
git push origin main vX.Y.Z
```

The release workflow validates the tag against the manifest, builds the package, and publishes files from `dist/GameplayConfigSync/`. Steam Workshop upload remains a local, user-confirmed step outside this repository.

## Definition of done

Before handing off a change:

- The requested behavior is implemented without weakening the safety invariants.
- Release build succeeds with zero errors.
- Relevant documentation and log contracts match the code.
- Multiplayer-sensitive changes include real-game test instructions or results.
- The diff contains no generated artifacts, local paths, credentials, raw player configuration values, or unrelated edits.
