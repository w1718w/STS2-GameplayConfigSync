# Steam Workshop upload

Double-click `scripts/OpenWorkshopUploader.cmd` to open the local upload window.

The window builds Release output, stages only `GameplayConfigSync.dll` and
`GameplayConfigSync.json`, then invokes Mega Crit's official Mod Uploader. It
always passes Workshop Item ID `3803257311`, so it cannot accidentally create a
second item. It does not write to the game's local `mods` directory or its Steam
subscription directory.

The official uploader is installed outside the repository at:

```text
../tools/sts2-mod-uploader/ModUploader.exe
```

Local upload state, the selected preview image, generated `workshop.json`, and
staged files live outside the repository under `../workshop-upload/`. They are
not release inputs and must not be committed.

Before uploading:

1. Keep Steam running and signed into the account that owns the Workshop item.
2. Enter a change note.
3. Optionally select a PNG preview smaller than 1 MB. Leaving it blank reuses the
   cached preview; on first use it falls back to Mega Crit's bundled default.
4. Review tags and visibility. Title and long description are deliberately left
   unchanged by the tool.

Use **Prepare only** to inspect the staged workspace without uploading.

To upload files downloaded from GitHub Release, select the folder containing
both `GameplayConfigSync.dll` and `GameplayConfigSync.json`, then use **Upload
existing release files**. This skips compilation and uploads those exact files.
