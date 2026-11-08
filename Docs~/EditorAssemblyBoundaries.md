# 技能编辑器的依赖边界

基础编辑器只引用 GAS Core、GAS Unity 和 GameplayTags。安装完整包时，运行时播放器在 `ZFramework.GAS.Unity.Realtime`，对应的创作工具在 `ZFramework.GAS.Editor.Realtime`。依赖从扩展指向基础；打开、校验或保存一个没有表现资产的技能，不需要播放器。

| 原耦合点 | 基础编辑器负责 | Realtime 编辑器提供 |
|---|---|---|
| 文档、资产工厂、索引与引用保护 | 遍历引用，复制或删除子资产，处理 Undo，通过 `IDefinitionAsset` 失效缓存 | 声明表现资产类型，创建内嵌动画，维护元素 ID，构建定义 |
| 结构图、流程、详情与布局 | 显示节点、连接效果、编辑动作、按稳定 ID 恢复选择和布局 | 端口可接受的资产类型、连接错误、片段字段、Cue 区间，以及标记的动作入口和时间 |
| 主窗口与模板 | 管理文档和面板生命周期；没有扩展时，结构图与详情占满各自区域，可以新建空白技能 | 每个窗口独立的时间轴、预览、角色及后端选择器；六种带动画的模板 |
| 校验与 Inspector | 通用动作、参数、效果规则，以及共用的策略选择控件 | 动画片段、区间、混合与后端校验；专属属性绘制器 |
| 运行时调试 | 采样能力、效果、属性、标签和动作，按任务快照更新跟随播放头 | 从活动动画任务读取已冻结的时长、段和权重，绘制任务条 |
| Player 构建 | 保留动作的具体类型，合并扩展声明的托管引用根类型 | 声明动画后端和运行时轨道类型，供 UnityLinker 保留 |

**扩展入口。** `Editor/SkillEditorExtensions.cs` 定义一个可选时间轴插槽。`RealtimeTimelineEditor` 在 Unity 域加载时注册；基础编辑器只通过接口访问已注册类型的资产，不按动画字段猜测任意 `GameplayAbilityAsset.Presentation` 的布局。`ISkillTimelineWorkspace` 的实例属于单个窗口，由窗口释放。校验规则和技能模板仍使用已有的 `TypeCache` 发现机制。

**引用方式。** 只用文档、结构图、动作、效果或基础校验的工具引用 `ZFramework.GAS.Editor` 即可。使用 `TimelineCommands`、`SkillTimelineAuthoring`、动画适配器或预览 API 的工具还需引用 `ZFramework.GAS.Editor.Realtime`；命名空间不变。BasicCombat 编辑器、Spine 编辑器和完整编辑器测试属于后一组。调试工具可从 `RuntimeSnapshot.Tasks` 读取通用的 `PresentationTask`；若需读取实时动画定义、段或混合权重，再匹配扩展中的 `AnimationTaskSnapshot`。

**验证边界。** 在临时 Unity 工程中仅导入 `Runtime/`（不含 `Runtime/Unity/Realtime/`）、`Editor/` 和 GameplayTags。用「空白技能」新建能力，打开结构图、流程和能力设置并保存；不应出现动画端口、时间轴或预览分栏。这个配置可以独立运行 `Tests~/Editor.Base/` 中的 EditMode 回归。再加入运行时与编辑器两个 Realtime 目录和 BasicCombat 示例，先用 `BasicCombatSample.RegisterTags()` 生成训练场的运行时标签表，再导入 `Tests~/Editor/` 运行 EditMode 测试，覆盖内嵌动画、标记导航、模板、预览和运行时跟随。只移除实时运行时却保留依赖它的示例、编辑器或测试，是无效的程序集组合。
