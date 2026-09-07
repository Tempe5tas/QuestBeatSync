# QuestBeatSync

> [!WARNING]
> QuestBeatSync is early software. The Phase 6 desktop, read, compatibility-gated write, and playlist workflow has completed its follow-up real-Quest acceptance. Phase 7A archives have not yet been manually validated on a real Quest.

QuestBeatSync (QBSync) is a preservation-first Windows and Linux desktop application for managing native Quest Beat Saber custom maps and PlaylistManager playlists through ADB. Normal sync never deletes existing Quest maps or playlists.

## Current workflow

At startup QBSync validates ADB and runs normal `adb devices` discovery. Connecting a USB or classic TCP ADB device selects/displays the device but does **not** automatically enumerate the full Beat Saber library.

The user may scan explicitly with **Scan Quest Library**. **Build Sync Plan** always performs its own mandatory fresh Quest library scan before resolving cache and BeatSaver requirements. If that scan fails, no plan is created and the last successful scan remains visible as stale/failed diagnostic state.

Current sync operations are executable only after preview and explicit confirmation. Execution plans are bound to the selected ADB serial, target Beat Saber package version, the exact scan fingerprint, immutable playlist source SHA-256 identities, and exact BeatSaver hash evidence. USB and network serials are deliberately distinct identities.

## Real canary findings and compatibility gate

The first real write canary proved that a structurally valid BeatSaver map can still be incompatible with the target Quest stack. On Beat Saber `1.35.0_8016709773` (`versionCode 1130`), a V4.0.1 map reproducibly caused a native SongCore crash during startup, while a Legacy V2 map launched successfully.

QBSync now parses only root-level `Info.dat` format properties and keeps BeatSaver availability separate from target compatibility. V4 maps are incompatible on Beat Saber versions earlier than 1.36. V4 maps on a missing/unparseable target version, or on a newer APK without proven SongCore capability, remain compatibility-unknown and are not uploaded. The exact prepared local bytes are inspected before a writer lock is acquired; a useful completed download may remain cached even when its upload is skipped.

The follow-up real Quest Phase 6 workflow has been accepted from a clean library baseline.

## Read-only backup archives (Phase 7A)

The **Backup** page creates an explicit, read-only local snapshot of these user-library sources:

- SongCore `CustomLevels`, including hash-named, legacy, unknown, nested, Unicode, and case-sensitive content.
- Every regular file below PlaylistManager `Playlists`, regardless of extension or whether QBSync understands it.
- Every regular file below PlaylistManager `Covers`, when present.
- SongCore `CachedSongData.json`, when present, as non-authoritative auxiliary state.

Files are pulled one at a time to generated host-safe staging names and hashed with streaming SHA-256. Exact bytes are stored as immutable content-addressed blobs under `blobs/sha256`; manifests retain the original Quest-relative filename and refer to blobs by hash. Identical files across snapshots reuse one blob, and existing blob contents are verified before reuse. Unknown or non-hash content is preserved because raw bytes—not parser success—are authoritative.

Backup performs a fresh scan before capture and another after capture using the same device serial, Beat Saber package version, and Quest scan fingerprint binding used by sync. If the source changes, no snapshot manifest is published. Completed manifests are published atomically only after all entries are pulled and committed. Snapshot verification is entirely local and checks manifest paths, duplicate logical paths, blob presence, size, and SHA-256.

Phase 7A performs zero Quest library writes: no push, deletion, move, directory creation, playlist import, writer lock, force-stop, or cleanup on the Quest. Restore is **NOT IMPLEMENTED**.

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

Large map writes use staged bulk scheduling. Eligible maps are uploaded sequentially into exact current-execution staging directories first; staging structure and promoted final structure are then validated in bounded batches of 32 to reduce short ADB shell round trips. The batch size keeps remote scripts bounded while reducing validation calls substantially for 100–200-map runs.

Promotion remains an independent no-clobber transaction per map using the existing `toybox mv -nT` capability requirement. Batching does not make the write set atomic: one failed map does not roll back unrelated successful maps, and cancellation preserves every already promoted final directory while cleaning only current-execution staging best-effort. No concurrent push swarm or new destructive behavior was introduced.

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

## Phase 7A manual backup acceptance checklist

This checklist is intentionally manual and has not yet been run for Phase 7A:

1. Start QBSync.
2. Confirm ADB Ready.
3. Note the scanned map and playlist counts.
4. Create a backup and verify the snapshot completes.
5. Run local **Verify Backup** and confirm it succeeds.
6. Confirm snapshot metadata matches the selected Quest.
7. Confirm a known hash map exists in the manifest.
8. If available, confirm at least one non-hash or unknown item is preserved.
9. Confirm playlists and covers are represented.
10. Confirm Quest content is untouched from the user's perspective and Beat Saber still launches.
11. Create a second backup without library changes and confirm blob reuse instead of full duplication.
12. Deliberately change one library item, create another backup, and confirm only changed/new blobs materially grow storage.
13. Do not test restore; restore does not exist in Phase 7A.

## Out of scope

Restore, archive deletion/garbage collection, destructive map or playlist updates, stale-lock recovery, `adb pair`, companion APKs, cloud sync, BeatSaver accounts, crash resume, and automatic Beat Saber launch are not implemented.
