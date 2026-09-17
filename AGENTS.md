# AGENTS.md

给在此仓库工作的 AI 编码代理的说明。人类贡献者同样适用。

## 项目

《杀戮尖塔 2》本地 Mod：联机时把主机的「玩法类」配置临时同步给客户端，仅内存生效，大厅/对局结束时还原。纯 DLL Mod（`has_pck: false`），依赖 BaseLib。

- C# / .NET 9 / Godot Mono，无第三方运行时依赖
- 目标游戏版本见 `GameplayConfigSync.json` 的 `min_game_version`

## 常用命令

```bash
dotnet build -c Release      # 构建，产物自动打包到 dist/GameplayConfigSync/
```

没有单元测试，也没有 linter。**验证 = 编译通过 + 在真实游戏里跑双端**，验收矩阵见 `docs/DESIGN.md`。编译成功和能进主菜单**都不构成**联网行为正确的证据。

## 硬性约定

### 版本号只有两处，必须一致

| 位置 | 用途 |
| --- | --- |
| `GameplayConfigSync.json` 的 `version` | 权威来源，游戏与 Mod 加载器读取 |
| `GameplayConfigSync.csproj` 的 `<Version>` | 仅供查看 DLL 属性 |

`build.yml` 与 `release.yml` 都会校验两者一致，不一致直接构建失败。

**README 正文里不写版本号。** 顶部两个徽章已经自动覆盖：`mod version` 取自最新 release tag，`game version` 取自 manifest 的 `min_game_version`。

### `docs/` 里的版本号是证据，不要「顺手更新」

`docs/RESEARCH.md`、`docs/DESIGN.md` 记录的是「核对过哪些具体版本」（游戏、BaseLib、RitsuLib 等）。那是历史事实，不是待维护的声明 —— 它们不会过期，改了反而丢失信息。

### 提交信息必须符合 Conventional Commits

`CHANGELOG.md` 由 `cliff.toml` 从提交信息生成。常用类型：`feat` / `fix` / `docs` / `build` / `refactor`。**`chore` 和 `ci` 会被跳过**，不进更新日志。

写漏前缀不会让改动消失 —— 配置里关掉了 `filter_unconventional`，它会落到「其他」分组里，看得见、好补。

### BaseLib 绝不打包进产物

csproj 中是 `PrivateAssets=all` + `ExcludeAssets=runtime`。游戏从创意工坊加载玩家实际安装的那一份，打包进来会导致加载冲突。

## 构建机制（改构建前必读）

游戏程序集（`sts2.dll` 等）不可再分发，所以构建分两条路，由 `Directory.Build.props` 的 `STS2GameDir` 自动选择：

- **本机装了游戏** → 引用真实 DLL，IntelliSense 与调试更好
- **没装（含 CI）** → 回落到 NuGet 的 `BSchneppe.Sts2.ReferenceAssemblies`（只有类型签名、无游戏代码）

两条路产出的引用表必须一致，否则 DLL 装进游戏会在运行时绑定失败。已实测：stub 与真实 `sts2.dll` 的程序集身份相同（`sts2 0.1.0.0`，无公钥标记）。**改动引用方式后应重新比对**。

**换游戏版本时，两处必须一起改：**

1. `GameplayConfigSync.json` 的 `min_game_version`
2. csproj 里参考程序集的 `Version`（包版本与游戏版本对齐，如 `0.111.0-beta-41cef1ea`）

### 产物结构是刻意嵌套的

```
dist/GameplayConfigSync/{GameplayConfigSync.dll, GameplayConfigSync.json}
```

套一层以 Mod ID 命名的目录，是为了同时匹配游戏要求的 `mods/GameplayConfigSync/` 和创意工坊的 content 结构。**不要「简化」成平铺**。

## 发版

```bash
# 1. 改上面那两处版本号
git commit -m "feat: ..."          # Conventional Commits
git-cliff --unreleased --prepend CHANGELOG.md
git commit -am "chore: 更新日志"
git push
git tag v0.4.0 && git push origin v0.4.0
```

推 tag 后 `release.yml` 自动构建、用 git-cliff 生成说明、把 `dist/GameplayConfigSync/` 下的产物发到 release。tag 与 manifest 版本不一致会被拦下。

**创意工坊上传无法自动化**：用的是游戏内工具，从本地模组列表读取，需要 Steam 登录。流程是「构建 → 把产物拷进游戏 `mods/` → 游戏内上传」。

## 环境注意（作者本机）

- Windows，shell 是 Git Bash
- **本机没有 `jq`** —— CI 工作流也因此改用 `grep -oP` 提取版本，别改回去
- `git-cliff` 通过 scoop 安装
- 网络：`github.com` / `raw.githubusercontent.com` 直连不通，但 `gh` CLI 正常可用；NuGet 会被重定向到国内镜像。**WebFetch 失败不代表资源不存在**，先用 `gh api` 确认
- README 的 `mod version` 徽章走 shields.io（`max-age=300`）与 GitHub camo 代理，**发版后最多滞后约 5 分钟属正常**，不要为它改 README

## 边界

- 仓库**有意不带许可证**（默认保留所有权利），不要"顺手"补一个
- 不要提交游戏程序集、BaseLib DLL、`dist/`、`logs/`（`.gitignore` 已覆盖）
