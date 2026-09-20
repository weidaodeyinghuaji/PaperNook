# PaperTodo V1.1 崩溃恢复测试矩阵

`tests/PaperTodo.CrashRecoveryChecks` 启动独立 `PaperTodo.CrashHarness` 进程，在可观察 barrier 到达后使用 `Process.Kill(entireProcessTree: true)` 终止。每个用例使用独立临时目录；通过后删除，失败时保留并打印路径。

| 场景 | 阶段 | 不变式 |
| --- | --- | --- |
| state-save | BeforeTempOpen / AfterTempWrite / AfterFlush / BeforeReplace | primary 为完整旧代或新代 |
| storage-config | AfterFlush | primary/backup 路由不出现半 JSON |
| backup-create | BeforeReplace | 可见 `.papernook-backup` 始终完整，临时文件不当作列表项 |
| restore-switch | BeforeOldMove / AfterOldMove / AfterNewMove | Data 或 before-restore 至少一代可恢复 |
| update-replace | BeforeOldMove / AfterOldMove / AfterNewMove | 目标或 previous 至少一份完整 EXE |
| plugin-health | Running / DiscoveringPlugins | 运行期断电不误归因；插件启动阶段可被准确记录 |

运行：

```powershell
dotnet run --project tests/PaperTodo.CrashRecoveryChecks/PaperTodo.CrashRecoveryChecks.csproj -c Release
```

该矩阵验证进程强制终止语义，不等同于物理断电。真实掉电测试只允许在可回滚 Windows VM 快照中执行，禁止在用户主机上模拟。
