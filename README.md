# STS2 Gameplay Config Sync

[English](README.en.md)

![mod version](https://img.shields.io/github/v/release/w1718w/STS2-GameplayConfigSync?label=mod%20version)
![game version](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Fw1718w%2FSTS2-GameplayConfigSync%2Fmain%2FGameplayConfigSync.json&query=%24.min_game_version&label=game%20version&color=blue)

> 个人自用的实验性项目。全部实现与文档均由 AI 协助编写 / 由 AI 生成，仅服务于作者本人的《杀戮尖塔 2》(Slay the Spire 2) 环境。本项目不作为稳定的社区发行版或受支持的产品提供。

《杀戮尖塔 2》本地 Mod，实现的是实验性联机协议。配置同步要求主机与客户端版本兼容；仅在单侧安装时，本 Mod 保持静默、不产生任何行为。由于本 Mod 依赖对游戏内部方法的 Harmony 补丁，游戏更新后需重新验证兼容性。

## 作用范围

- 读取通过 BaseLib 注册的配置对象。
- 在安装了 RitsuLib 时，读取通过 RitsuLib 注册的设置绑定。
- 只包含"由已加载 Mod 注册、且其 manifest 声明了 `affects_gameplay: true`"的配置项。
- 仅在内存中把主机快照应用到联机客户端（或只记录一次 dry run 日志）。
- 大厅 / 对局结束时，恢复客户端原有的内存值。
- 在游戏正常退出流程之前完成恢复，以免 BaseLib 在退出时把临时的主机值写入存档。
- 在游戏现有的握手机制中附带一个极小的能力标记；除非主机也声明了相同协议，否则不发送任何自定义消息。
- 使用协议版本、请求关联、负载哈希、大小限制、主机身份校验，以及结构化的 `GCS|...` 日志事件。

当前 BaseLib 与 RitsuLib 的 API 均未暴露"逐设置项"的玩法元数据。因此，Mod 级别的 manifest 标记是现有可用的最窄的权威分类口径；本项目不做任何基于名称的猜测。

同步器自身的 `GameplayConfigSync` 设置被明确排除在快照之外。其 BaseLib 设置项如下：

- `Enabled`：总开关。
- `DryRun`：只校验并记录快照，不实际应用取值。
- `Diagnostics`：普通或详细的结构化日志。
- `FileLogging`：每次游戏启动写入一个独立的 UTF-8 日志；默认开启。
- `AllowUnsafeRitsuBindings`：允许使用持久化行为无法被证明安全的 RitsuLib 绑定；默认关闭。

RitsuLib 目前并未提供"临时写入且不持久化"的通用公开操作。因此默认采取 fail-closed（失败即关闭）策略：临时绑定与已知的自动存档兼容适配器会被接受；未知的绑定实现会被跳过，除非启用上述不安全选项。

`affects_gameplay` 为 false，因为本 Mod 自身不提供任何卡牌、规则、模型或平衡性设置。与 AutoModSubscriber 和 Multiplayer Mod Sync 类似，它使用 `PeerVersionInfo.otherMods` 中的一个可移除条目作为能力 sidecar。客户端只有在读到匹配的主机标记后，才会发送 BaseLib 自定义请求。因此，仅在单侧安装时既不会执行同步，也不会发送未知的自定义消息。被纳入快照的第三方设置，仍然只来自 manifest 声明了 `affects_gameplay: true` 的 Mod。

## 联机测试日志

在两端搜索 `logs/godot.log` 中的 `GCS|`。匹配同一个 `request=` 值，并核对事件顺序：

1. 客户端 `CAPABILITY_OK`，随后 `REQUEST_SEND`
2. 主机 `SNAPSHOT_CAPTURE` 与 `SNAPSHOT_SEND`
3. 客户端 `SNAPSHOT_APPLY_OK`（或 `SNAPSHOT_DRY_RUN`）
4. 客户端在断线或对局清理时 `RESTORE_OK`

主机与客户端的 `sha256=` 前缀必须一致。日志只记录标识与计数，不记录原始配置取值。

当 `FileLogging=true` 时，同样的事件也会写入 `user://GameplayConfigSync/logs/gameplay-config-sync-YYYYMMDD-HHmmss.log`。文件名和每行的 `time=` 均使用带时区偏移的本地时间；每行还包含单调递增序号、严重级别与事件代码。

若进行单侧测试，应看到 `CAPABILITY_MISSING` 与 `REQUEST_SKIPPED`，且不出现 `REQUEST_SEND` 或任何快照事件。

证据 / 来源清单与开发流程见[研究笔记](docs/RESEARCH.md)，当前安全边界与双端测试矩阵见[设计笔记](docs/DESIGN.md)，各版本改动见[更新日志](CHANGELOG.md)。

## 仓库结构

- `src/` —— Mod 源码。
- `GameplayConfigSync.json` —— Mod manifest；版本号与游戏版本要求的权威来源。
- `GameplayConfigSync.csproj`、`Directory.Build.props` —— 构建工程。
- `.github/workflows/` —— 推送时构建，打标签时自动发布。
- `cliff.toml` —— 更新日志的生成配置。
- `docs/` —— 设计、安全、研究与测试笔记。
- `AGENTS.md` —— 给 AI 编码代理的仓库说明：构建机制、硬性约定、发版流程。
- `dist/GameplayConfigSync/` —— 构建产物；有意排除在 Git 之外。

## 构建

```
dotnet build -c Release
```

产物落在 `dist/GameplayConfigSync/`，含 `GameplayConfigSync.dll` 与 `GameplayConfigSync.json`。把整个目录拷进游戏的 `mods/` 下即可，最终路径为 `mods/GameplayConfigSync/`。

本地构建默认引用你机器上安装的游戏程序集，路径在 `Directory.Build.props`；游戏装在别处就用 `dotnet build -c Release -p:STS2GameDir="D:/你的路径"` 覆盖。没装游戏的机器 —— 包括 CI —— 会自动改用 NuGet 上只有元数据的编译用参考程序集，因此不需要游戏也能构建。

发布由 CI 完成：推送一个 `v*` 标签，工作流会构建并把产物附到对应的 release 上。
