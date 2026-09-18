# 更新日志

本项目所有值得注意的变更都记录在此文件。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
提交信息遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

<!-- git-cliff: end of header -->
## [0.4.0-beta.1] - 2026-09-18

> 预发布版：已通过 Release 编译，尚未完成本版本的双端实机回归测试。

### 新增

- 为游戏内配置加入简体中文本地化（[eaed958](https://github.com/w1718w/STS2-GameplayConfigSync/commit/eaed958938a76837cdcff03485517189d8518eeb)）

### 修复

- 消除 LoadRunLobby 构造函数链导致的重复挂载（[b1d57cb](https://github.com/w1718w/STS2-GameplayConfigSync/commit/b1d57cba79d48acbc69bfc8c0d42fec2542a81d8)）
- 日志改为每次游戏会话单独写入，并使用本地时间（[2cde6fe](https://github.com/w1718w/STS2-GameplayConfigSync/commit/2cde6fe2f296e17a2e9d091c60afe1a74d6fef23)）

### 重构

- 按职责拆分同步器源码，保持编译产物行为一致（[a0bea24](https://github.com/w1718w/STS2-GameplayConfigSync/commit/a0bea249d8ef2c7c11e57b60fa10307732d9089a)）

### 构建与文档

- 产物改为与 Mod 安装结构一致的嵌套目录。
- 重写仓库协作指南，并补充构建与发布约定。

## [0.3.0] - 2026-09-17

### 新增

- 初始实现 0.3.0 玩法配置同步（[e5179ed](https://github.com/w1718w/STS2-GameplayConfigSync/commit/e5179ed50ba5664f7537fd0cd6835a8177c4f60c)）

### 构建

- 迁移到 csproj 与 GitHub Actions 自动构建（[798aaa7](https://github.com/w1718w/STS2-GameplayConfigSync/commit/798aaa7e641d86a91a4bfb4b2ff1fe085597562d)）

### 文档

- 中文 README 为主，新增英文版（[32d447e](https://github.com/w1718w/STS2-GameplayConfigSync/commit/32d447e325d8b938bf04f0101bb7db823e4d6deb)）
