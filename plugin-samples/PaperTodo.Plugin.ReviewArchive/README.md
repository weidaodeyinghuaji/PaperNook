# 待办复盘记录池

这是一个面向 PaperTodo Protocol 2.1 的完整原生插件。它通过 provider 级 Plugin Runtime 持续监听待办事件，并使用宿主标准 presentation 展示胶囊；它不提供专属 Edge Mini，由宿主放大结构化胶囊作为快速浏览内容。

## 记录内容

- 新建待办的创建时刻；
- 待办完成、取消完成、删除和恢复的时刻；
- 正文与所属纸片标题变化；
- 提醒设置、取消和调整事件，以及当前提醒时间；
- 来源是否已经删除；
- 用户、MCP、插件等事件来源。

## 新版展示

- 今日完成、近 7 天完成、连续完成日和进行中数量；
- “重新打开”和“有提醒”独立筛选；
- 未来 24 小时提醒与已到期提醒高亮；
- 胶囊可显示累计完成、今日完成、连续完成日或进行中数量；开启“显示复盘指标”时附带进行中数量，并按当前可见指标自动适配宽度；
- CSV 补齐完成次数、最后重新打开、提醒时间和提醒变更次数，并修复旧版表头与数据列错位。

记录池保存在插件目录的 `.runtime/review-archive.json`，不属于任何一张纸片的 `StateJson`。升级时会将存储版本 1/2 自动迁移到版本 3，原有记录与事件不会丢失。

只要仍存在一张复盘插件 Paper，provider Runtime 就会持续监听；纸片隐藏、折叠或当前没有窗口都不会中断记录。删除或切换掉最后一张复盘插件 Paper、退出 PaperTodo 后 Runtime 才结束，停用期间发生的变化无法被精确补记；再次打开后可用“导入当前”补录现状，但补录时间会标记为“首次观察值”。持续监听和自动记录由 provider Runtime 负责；正文 Session 负责读取、筛选与展示记录，并处理用户主动发起的“导入当前”、清空和 CSV 导出。示例对 PaperTodo 数据的订阅与读取统一使用 canonical `context.Workspace`。

## 构建并安装

先完全退出 PaperTodo，再从仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.ReviewArchive\PaperTodo.Plugin.ReviewArchive.csproj
```

安装目录：

```text
plugins\sample.review-archive.native\
```
