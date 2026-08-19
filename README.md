# QuestBeatSync

> [!WARNING]
> QuestBeatSync is early software. A real Quest canary was performed and exposed target-map compatibility and playlist-identity failures. Phase 6 is **not accepted** until the follow-up canary passes.

QuestBeatSync (QBSync) is a preservation-first Windows and Linux desktop application for managing native Quest Beat Saber custom maps and PlaylistManager playlists through ADB. Normal sync never deletes existing Quest maps or playlists.

## Current workflow

At startup QBSync validates ADB and runs normal `adb devices` discovery. Connecting a USB or classic TCP ADB device selects/displays the device but does **not** automatically enumerate the full Beat Saber library.

The user may scan explicitly with **Scan Quest Library**. **Build Sync Plan** always performs its own mandatory fresh Quest library scan before resolving cache and BeatSaver requirements. If that scan fails, no plan is created and the last successful scan remains visible as stale/failed diagnostic state.

Current sync operations are executable only after preview and explicit confirmation. Execution plans are bound to the selected ADB serial, target Beat Saber package version, the exact scan fingerprint, immutable playlist source SHA-256 identities, and exact BeatSaver hash evidence. USB and network serials are deliberately distinct identities.

## Real canary findings and compatibility gate

The first real write canary proved that a structurally valid BeatSaver map can still be incompatible with the target Quest stack. On Beat Saber `1.35.0_8016709773` (`versionCode 1130`), a V4.0.1 map reproducibly caused a native SongCore crash during startup, while a Legacy V2 map launched successfully.

QBSync now parses only root-level `Info.dat` format properties and keeps BeatSaver availability separate from target compatibility. V4 maps are incompatible on Beat Saber versions earlier than 1.36. V4 maps on a missing/unparseable target version, or on a newer APK without proven SongCore capability, remain compatibility-unknown and are not uploaded. The exact prepared local bytes are inspected before a writer lock is acquired; a useful completed download may remain cached even when its upload is skipped.

The follow-up real Quest write path is **not yet validated**.

## Playlist identity and lifecycle

Quest `.bplist`, JSON, and PlaylistManager `_BMBF.json` representations are normalized using exact BeatSaver hashes where available, with keys used only as a fallback. Equal titles or equal song counts alone never prove equality.

- A semantically equal Quest playlist is kept and no second file is imported.
- A same-lineage/title playlist with changed contents is reported as a conflict; the existing Quest file is preserved because safe replacement is not implemented.
- Ambiguous identity is preserved and not duplicated opportunistically.
- A genuinely new playlist is transferred using its original safe `.bplist` basename, such as `BeatSaver - ACG.bplist`.

QBSync does not synthesize `_BMBF.json`. PlaylistManager may perform its normal post-launch conversion to a name such as `BeatSaver - ACG.bplist_BMBF.json`. Historical `qbsync-*` canary artifacts are recognized through semantic comparison but are never automatically deleted or renamed.

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
3. Target map-format compatibility preflight against the bound Beat Saber package version.
4. A pre-write Quest scan.
5. Cooperative writer-lock acquisition and Beat Saber force-stop.
6. A lock-held Quest scan and fingerprint validation.
7. Upload/import operations only if all validation succeeds.

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

## Follow-up manual canary checklist

This checklist is intentionally manual and has not yet passed:

1. Start QBSync.
2. Confirm ADB Ready.
3. Discover/connect the Quest through the UI.
4. Confirm connection alone does not trigger a full library scan.
5. On Beat Saber 1.35, verify V4.0.1 regression map `73F85C364E9C4EF7EE99FFF551BD3431D237B209` is classified incompatible and receives zero uploads.
6. Verify Beat Saber still launches and that map is absent from `CustomLevels`.
7. Import and execute one known-compatible Legacy V2 map; verify launch, visibility, and playability.
8. Verify a semantically equal existing ROCK `_BMBF.json` causes zero playlist imports and no duplicate ROCK.
9. Verify changed ACG content is reported as update-required/conflict, with the existing ACG preserved and no duplicate created.
10. Import one genuinely new playlist and verify its ingress uses the original `.bplist` basename.
11. Manually launch Beat Saber and observe PlaylistManager's native `_BMBF.json` conversion.
12. Verify no current execution staging remains, the writer lock is gone, old Quest-only maps remain, and Delete is 0.

## Out of scope

Backup/restore, destructive map or playlist updates, stale-lock recovery, `adb pair`, companion APKs, cloud sync, BeatSaver accounts, crash resume, and automatic Beat Saber launch are not implemented. Phase 7 has not started.
