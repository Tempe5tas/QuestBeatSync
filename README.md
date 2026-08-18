# QuestBeatSync

> [!WARNING]
> QuestBeatSync is early software. Its real Quest write path is **NOT YET MANUALLY VERIFIED**. Use only a dedicated, small canary playlist for the first manual write test.

QuestBeatSync (QBSync) is a preservation-first Windows and Linux desktop application for managing native Quest Beat Saber custom maps and PlaylistManager playlists through ADB. Normal sync never deletes existing Quest maps or playlists.

## Current workflow

At startup QBSync validates ADB and runs normal `adb devices` discovery. Connecting a USB or classic TCP ADB device selects/displays the device but does **not** automatically enumerate the full Beat Saber library.

The user may scan explicitly with **Scan Quest Library**. **Build Sync Plan** always performs its own mandatory fresh Quest library scan before resolving cache and BeatSaver requirements. If that scan fails, no plan is created and the last successful scan remains visible as stale/failed diagnostic state.

Current sync operations are executable only after preview and explicit confirmation. Execution plans are bound to the selected ADB serial, the exact scan fingerprint, immutable playlist source SHA-256 identities, and exact BeatSaver hash evidence. USB and network serials are deliberately distinct identities.

## ADB bootstrap

Every ADB candidate is validated by running `adb version`. Selection order is:

1. A valid manually selected executable.
2. A valid QBSync-managed Platform-Tools installation.
3. A valid system/PATH executable.

If ADB is missing, the user may explicitly choose **Download Platform-Tools**. QBSync downloads the official Android Platform-Tools archive into per-user application data, validates the extracted executable, and only then activates the versioned installation. It never downloads silently and never changes PATH, the registry, or shell profiles. A failed installation cannot replace a working selection.

USB discovery and classic TCP wireless ADB are supported. A selected connected USB Quest may run `adb -s SERIAL tcpip 5555`; the user then enters the Wi-Fi address and QBSync runs `adb connect HOST:PORT`, followed only by `adb devices` refresh. Pairing-code `adb pair` is not implemented.

## Write safety

QBSync prepares network/cache inputs and immutable playlist snapshots before opening a Quest write session. Execution performs:

1. Initial validation against the confirmed scan binding.
2. Local preparation.
3. A pre-write Quest scan.
4. Cooperative writer-lock acquisition and Beat Saber force-stop.
5. A lock-held Quest scan and fingerprint validation.
6. Upload/import operations only if all validation succeeds.

A mismatch at any validation refuses execution without map or playlist content writes and asks the user to rebuild. The writer lock is released best-effort; journal and cleanup failures remain diagnostic warnings and cannot rewrite primary operation truth.

Map uploads use QBSync-owned staging, validate basic structure, and use no-clobber promotion. Existing final maps and existing PlaylistManager destinations are preserved. A pre-existing writer lock is never automatically removed as stale.

## Build and test

Requires the .NET 10 SDK:

```powershell
dotnet restore QuestBeatSync.sln
dotnet build QuestBeatSync.sln
dotnet test QuestBeatSync.sln
dotnet run --project src/QuestBeatSync.App/QuestBeatSync.App.csproj
```

Automated tests use fakes for Quest, ADB, and network boundaries. They must not access or mutate a real Quest, use the real internet, or depend on a locally installed ADB.

## Manual single-map canary checklist

This checklist is intentionally manual and has not yet been completed:

1. Start QBSync.
2. Confirm ADB Ready.
3. Discover/connect the Quest through the UI.
4. Confirm connection alone does not trigger a full library scan.
5. Import a dedicated canary playlist containing one small map not currently installed.
6. Build Sync Plan.
7. Confirm the fresh scan reflects the real existing Quest library.
8. Verify the preview shows Download: 1, Upload: 1, Playlist transfer: 1, Delete: 0.
9. Confirm and execute.
10. Verify the final `CustomLevels/<HASH>` exists, no current execution staging remains, the writer lock is gone, and old Quest-only maps remain.
11. Manually start Beat Saber.
12. Verify and play the map.
13. Inspect PlaylistManager behavior.
14. Build the same plan again and observe idempotency behavior.

## Out of scope

Backup/restore, destructive map or playlist updates, stale-lock recovery, `adb pair`, companion APKs, cloud sync, BeatSaver accounts, crash resume, and automatic Beat Saber launch are not implemented. Phase 7 has not started.
