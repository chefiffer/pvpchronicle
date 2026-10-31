# PvP Chronicle · 战场纪事

[English](README.md) | [简体中文](README.zh-CN.md)

PvP Chronicle 是用于记录和复盘《最终幻想 XIV》PvP 战局的 Dalamud 插件。
它支持纷争前线和水晶冲突的本地战绩、详细复盘、地图机制、数据统计与具有
明确证据边界的行为完整性复核。

## 安装

1. 安装并启动 XIVLauncher，确认 Dalamud 已启用。
2. 打开 **Dalamud 设置 → 测试版**。
3. 添加下面的自定义插件仓库地址：

   ```text
   https://raw.githubusercontent.com/chefiffer/pvpchronicle/main/repo.json
   ```

4. 打开插件安装器，搜索 **Battlefield Frontline Collector** 并安装。

## 主要功能

- 纷争前线与水晶冲突的本地战局记录和详细复盘。
- 比分、KDA、战斗流、死亡、移动、标点和可观测地图机制时间线。
- PvP 月历、地图轮换、地图专属机制与八语言本地化界面。
- 可选上传战局，以获取服务器生成的 Logs 和历史参考分布。

PvP Chronicle 不会自动操作游戏、代替玩家释放技能、揭示客户端无法观测的
隐藏状态，也不会把不完整的观测直接当作作弊定论。

## 参与贡献

Fork 本仓库，为单一改动创建分支，补充相应测试，然后向 `main` 提交 Pull
Request。

请勿提交真实战局包、玩家数据、访问令牌、私有服务器地址或其他玩家的个人
信息。安全问题请按照 [`SECURITY.md`](SECURITY.md) 私下报告。

## 许可证

本仓库中的 PvP Chronicle 用户端源码采用
[GNU Affero General Public License v3.0](LICENSE) 许可证。

该许可证仅适用于 PvP Chronicle 原创用户端源码及明确标记为
AGPL-3.0-only 的其他文件，不会把《最终幻想 XIV》素材、第三方程序库或
字体重新授权为 AGPL；这些内容分别受其权利人和对应许可证约束。

《最终幻想 XIV》的名称、图像和图标等素材仍归 Square Enix 所有。游戏素材
署名及第三方许可证说明见
[`plugin/THIRD-PARTY-NOTICES.md`](plugin/THIRD-PARTY-NOTICES.md)。

PvP Chronicle 是独立、非商业的社区项目，与 Square Enix 不存在隶属或官方认可关系。
