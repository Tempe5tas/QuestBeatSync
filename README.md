> [!WARNING]
> **QuestBeatSync 还没有成型，目前是早期开发版本。**
> 功能、数据格式和界面都可能发生不兼容变化。当前版本已经包含首个真实 ADB 同步执行路径，但尚未完成真实 Quest 人工验收；**请只使用小型 canary 歌单谨慎测试，不应把它当作完整或稳定的 Quest 曲库管理工具。**

# QuestBeatSync

## ADB bootstrap (Phase 6B.2)

QBSync validates every candidate by running `adb version`. It prefers a valid manually selected executable, then a previously installed QBSync-managed copy, then an existing system/PATH installation. The Settings page shows the selected source, version, and exact executable path.

If ADB is missing, the user may explicitly choose **Download Platform-Tools**. QBSync downloads the official Android SDK Platform-Tools package into its per-user application-data directory, validates the extracted executable, and only then activates the versioned installation. This is never an automatic startup download and does not modify the system PATH, registry, or shell profiles. Manual executable selection and return to automatic selection are also supported.

USB discovery and classic TCP wireless ADB are available in the UI. A selected, connected USB Quest can run `adb -s SERIAL tcpip 5555`; the user then enters the Quest's Wi-Fi address and QBSync runs `adb connect HOST:PORT`, followed by normal `adb devices` discovery. USB and network serials remain distinct device identities. Android pairing-code `adb pair` is not implemented.

The real Quest write path remains **not manually verified** until the documented full-UI single-map canary is actually completed.

QuestBeatSync（QBSync）是一个面向 Windows 和 Linux 的桌面工具，目标是通过 ADB 安全管理 Meta Quest 原生 Beat Saber 的自定义歌曲与 PlaylistManager 歌单。

项目坚持非破坏性、可预览的增量同步：所有可能修改 Quest 文件的操作都必须先生成并展示 `SyncPlan`，正常同步永远不会删除 Quest 上已有的自定义谱。

## 当前进度

目前已经实现：

- 检测 PATH、应用数据目录或用户配置路径中的 ADB。
- 枚举 USB / 网络 Android 设备，处理 unauthorized、offline、多设备和超时状态。
- 只读探测 Beat Saber、SongCore、CustomLevels 和 PlaylistManager 环境。
- 只读扫描 Quest 自定义谱与 PlaylistManager 歌单。
- 导入一个或多个 UTF-8 `.bplist`，支持中文、日文、大型封面和重复引用统计。
- 按 BeatSaver exact hash 查询谱面；缺少 hash 时才使用 map key。
- 区分 Online、Unavailable 和 Unknown，不把网络异常当成谱面下架。
- 将谱面原子下载到本地缓存，验证 `Info.dat` 后才标记为完整。
- 生成非破坏性的同步预览，包括下载、上传、歌单导入、保留及跳过操作。
- 将执行计划绑定到指定 Quest、扫描指纹和不可变歌单快照，经二次确认后执行。
- 通过 QBSync staging 目录上传谱面、验证基本结构并进行 no-clobber promotion。
- 将准备好的歌单快照传输到 QBSync 管理的确定性远程文件名。
- 显示 operation 级进度、取消和执行结果；已完成操作不会因取消而回滚。

目前尚未实现：

- 删除 Quest 上的歌曲。
- Beat Saber Mod 安装、降级或 BMBF / MBF 替代功能。
- BeatSaver 登录、在线歌单编辑、PC Beat Saber、Score / Replay 或 Mod 下载管理。
- 可供普通用户直接安装的 self-contained 发布包。

## 安全原则

- 默认不删除 Quest 上已有的自定义谱。
- 不会根据歌曲名或 mapper 猜测谱面身份。
- 谱面首要身份是 SHA1 / BeatSaver version hash，map key 仅作为次要身份。
- BeatSaver 404、网络错误、未知来源谱和本地独有谱分别处理。
- Quest 上无法从 BeatSaver 重新获取的谱仍会保留。
- 多设备连接时不会静默选择第一台设备。
- 所有 ADB 调用都经过统一 transport abstraction。
- 单元测试不访问真实 Quest。

## 技术栈

- C# / .NET 10
- Avalonia UI 11
- MVVM
- `System.Text.Json`
- `HttpClient`
- `System.IO.Compression`
- JSON manifest 与文件缓存；当前不使用数据库

首要目标平台是 Windows x64 和 Linux x64。

## 从源码运行

开发环境需要：

- .NET 10 SDK
- 可选：Android Platform Tools 中的 `adb`
- 可选：开启开发者模式并允许 USB 调试的 Quest

克隆仓库后运行：

```powershell
dotnet restore QuestBeatSync.sln
dotnet run --project src/QuestBeatSync.App/QuestBeatSync.App.csproj
```

ADB 可执行文件的查找顺序是：

1. Settings 中配置的路径。
2. 应用数据目录下 `QuestBeatSync/tools/adb`（Windows 上为 `adb.exe`）。
3. 系统 `PATH` 中的 `adb`。

应用不会自动下载 ADB。

## 使用当前原型

1. 启动应用，在 Dashboard 检查 ADB 与设备连接状态。
2. 如果连接了多台设备，明确选择要读取的设备。
3. 等待 Beat Saber 环境只读扫描完成。
4. 在 Playlists 页面导入 `.bplist`。
5. 按需查询 BeatSaver，并将可用谱面缓存到本机。
6. 打开 Sync 页面，点击 **Build Sync Plan**。应用会自动汇总全部已导入歌单、按 hash 去重，并解析生成计划所需的 BeatSaver 状态。
7. 检查统计和每一条 operation；当前版本不会执行这些 operation。

## 本地数据

应用数据位于操作系统的 Local Application Data 目录下：

```text
QuestBeatSync/
├── settings.json
├── tools/
├── cache/
    └── maps/
        └── <SHA1>/
├── executions/             # 当前执行的临时不可变歌单快照
└── execution-journal/      # 诊断历史，不用于自动恢复
```

谱面先下载并解压到临时目录。只有通过基本完整性检查并写入完成标记后，目录才会被原子移动到正式 `<SHA1>` 缓存位置。异常退出留下的临时目录不会被视为有效缓存。

执行时，歌单会先复制到 execution-owned snapshot；后续 ADB 传输只读取该 snapshot。正常结束或取消后会 best-effort 删除 snapshot，崩溃遗留不会被自动恢复为同步计划。

## 构建与测试

```powershell
dotnet build QuestBeatSync.sln
dotnet test QuestBeatSync.sln
```

测试覆盖 ADB 输出解析、只读 Quest 文件系统扫描、`.bplist` 导入、BeatSaver 错误分类、原子缓存和纯函数同步规划。涉及真实 Quest 的操作应放在明确启用的 integration test 中。

## 项目结构

```text
src/
├── QuestBeatSync.App/             # Avalonia UI 与 ViewModels
├── QuestBeatSync.Core/            # Domain models 与纯业务逻辑
└── QuestBeatSync.Infrastructure/  # ADB、BeatSaver、缓存与文件解析

tests/
└── QuestBeatSync.Tests/           # 单元测试与 fixtures
```

## SyncPlan 行为

当前 planner 只生成以下操作：

- `DownloadMap`
- `UploadMap`
- `ImportPlaylist`
- `KeepExisting`
- `PreserveQuestOnly`
- `SkipUnavailable`
- `SkipUnknown`

不存在 `DeleteMap`。Sync 页面会始终显示 `Deletion: 0`，并明确提示：

> QBSync never deletes existing maps during normal sync.

## Phase 6B.1 write-session safety

> **NOT YET MANUALLY VERIFIED ON REAL QUEST**

QBSync currently uses a cooperative single-writer lock under Beat Saber's ModData directory. Network downloads and immutable playlist snapshot preparation finish before this lock is acquired. The Quest is scanned again, the lock is acquired with one atomic `mkdir`, and Beat Saber is force-stopped immediately before the write phase.

QBSync instances that respect this lock do not write concurrently, and normal sync never intentionally overwrites an existing final map destination. Toybox `mv -n` is not described as a universal atomic no-replace primitive. External ADB clients and file-management tools are outside the cooperative guarantee, so users must not modify Beat Saber ModData with another tool while synchronization is running.

An existing `.qbsync-write-lock` causes sync to refuse writes. QBSync does not automatically remove a possibly stale lock. Lock and current-execution staging cleanup are best-effort diagnostics and cannot change an already completed operation result.

Managed playlist destinations are still preserved rather than replaced. Exact remote-content idempotency is not implemented yet, so an existing managed playlist file is conservatively refused even when it may contain the same bytes.
