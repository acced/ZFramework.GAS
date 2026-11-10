# 验证

## Core：可在无 Unity 环境运行

将 GameplayTags 1.0.0 仓库放在 GAS 仓库的相邻目录 `ZFramework.GameplayTags`。CI 固定使用 `8dde1a43485f33031fd73b4e6cb21304f03c19df`，避免依赖浮动分支。

```bash
python3 Tools~/verify_architecture.py
dotnet build Tests~/Core/Core.csproj -c Release -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet .build~/tests/bin/Release/net8.0/Core.dll
```

覆盖属性聚合、标签门禁、消耗与冷却、延迟提交、效果堆叠/周期/到期/子效果、事件/Cue、任务、取消回调重入、代际身份、不可变参数和分配。当前基线为 35 个 PASS 组。

分配检查测量热身后的指定 .NET 执行路径。主要场景：10,000 次真实消耗/冷却/DoT、10,000 次手动激活/提交/冷却、100,000 次激活/结束。各路径断言 0 B；它们不是 Unity 全功能零分配或目标设备性能的证明。

## Unity：需要真实工程

Tests~ 默认不导入。将需要的测试目录复制到测试工程 Assets 下，或用测试专用包布局导入；保持 asmdef 引用与本包一致。

- `Editor.Base`：不引用实时表现层，验证资产校验、错误定位与基础编辑器接入。
- `Editor`：动作、逻辑/物理目标、参数、生命周期、编辑器 Undo/引用、时间轴与运行时调试。
- `PlayMode`：真实 Playables、Cue/VFX 生命周期和自足播放用例。

删除了旧多动画后端、模块、套组、弹射、投射物、区域、BasicCombat、专用截图及 Windows 样例烟测相关测试。保留能力/效果/目标身份/任务取消等语义回归；已取消功能不保留兼容专测。当前可选 Spine 集成的测试随独立包提供，见 [Spine 集成验证](../Integrations~/Spine/README.md#验证)。公共 Editor 的 AnimationClipEditorTests 覆盖资源适配、Undo、校验、采样器释放和初始化失败恢复。

当前自动化环境没有 Unity Editor。因此 Unity 测试是待执行的回归集合，不能标为已通过。发行前需在 Unity 6000.3 完成 EditMode/PlayMode、编辑器实际操作与目标平台构建。

## 静态检查的作用

`Tools~/verify_architecture.py` 校验程序集依赖、Core 无引擎引用、基础 Unity 无物理实现、metadata、样例可选性。Roslyn C# 9 可检查 Editor/Player 两种预处理条件下的语法；它不解析 Unity 类型，也不替代 Unity 编译。
