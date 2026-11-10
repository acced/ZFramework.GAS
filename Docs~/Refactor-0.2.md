# 0.2 精简与迁移

本文记录 0.2 的删除与迁移结果。0.3 在同一时间轴上增加了独立可选的 [Spine 适配包](../Integrations~/Spine/README.md)，并将动画资源入参从 AnimationClip 放宽为 UnityEngine.Object；旧的多后端管理器没有恢复。

本轮在 `46652a7` 的第一次分层重构上继续删除功能。删除的是公开 API、数据模型、运行时实现和对应编辑器入口；不以隐藏菜单或保留兼容转发代替删除。

## 删减规模

基准为第一次重构提交 `46652a7`；统计 C# 物理行，包含注释与空行，生产范围排除 Tests~、Docs~、Samples~。

| 范围 | 改前 | 改后 | 净删 |
| --- | ---: | ---: | ---: |
| 默认生产 C# 文件 | 133 | 100 | 33 |
| 默认生产 C# 行 | 16,592 | 11,317 | 5,275（31.8%） |
| Core 行 | 2,756 | 2,547 | 209 |
| Unity 基础层行 | 1,985 | 1,408 | 577 |
| Realtime/Spine runtime 行 | 2,927 | 1,054 | 1,873 |
| 全部 Editor 行 | 8,489 | 6,172 | 2,317 |
| 样例 C# 行 | 3,484 | 40 | 3,444 |
| 基础 Editor 菜单入口 | 8 | 2 | 6 |

生产代码与样例合计净删 8,719 行。测试与过时报告的删除不用于放大生产代码降幅。

## 删除范围

| 删除内容 | 原因 | 项目接入方式 |
| --- | --- | --- |
| AbilitySetAsset、多来源 Grant 栈 | 装备/职业/背包来源归属是项目规则 | 项目保存返回的 Spec，明确 Grant / Revoke |
| SkillActionModuleAsset、共享参数集、模块调用/提取/内联、参数父链 | 形成第二套解释器与作用域系统 | 技能自身声明参数；代码复用派生 SkillAction |
| SetByCaller 字典与声明 UI | 与强类型参数重复 | EffectEvaluationContext + FromParameter |
| SpawnPrefabAction、LaunchProjectileAction、CreateAreaAction、TargetSequence/NextTargetStep | 投射物/区域/弹射属于玩法 | 项目 SkillAction / GameplayAbilityTask |
| 对象生成服务、SceneSkillTaskHost MonoBehaviour | 代替项目做了对象池和全局 Tick 决策 | 项目明确实例化、持有/推进 GameplayTaskHost |
| AttributeFormula | 硬编码具体伤害算法 | IEffectCalculation |
| Animator/Legacy/Sprite/Spine 多后端 | 技能库兼任动画框架 | 内置 Playables；项目可实现 IAnimationDriver |
| SkillAnimator、SkillAnimationManager、ClipOverride、RootMotionRelay、PoseJobs、自定义 blend 系统 | 多入口与角色动画系统职责重叠 | 项目 Animator/移动系统负责 |
| Section、Jump/Queue、输入缓冲、镜头/顿帧/LookAt/变速/曲线轨 | 连招和演出策略属于项目 | 显式动作、事件、任务或自定义轨道 |
| 批量/差异/全局查找/引用/模块窗口、模板注册器 | 维护成本超过最小创作路径需要 | 主窗口、Inspector、版本控制 |
| 图书签、小地图、注释组、重路由等辅助功能 | 非技能编排必要能力 | 保留连线、选择、自动布局与 Undo |
| 旧 BasicCombat 演示及专用截图/烟测/过时性能报告 | 将角色、美术、玩法验证绑进工具库 | 一个最小 AbilityActor 接入示例 |

编辑器预览也删除了模拟弹射/投射物/区域的第二套战斗执行器。现在只预览姿势、VFX、音效与几何；真实玩法在 Play Mode 验证。

## API 迁移

| 原调用 | 新调用 |
| --- | --- |
| Runner.TryCast(asset, ASC, taskContext, overrides) | Runner 构造时注入服务；TryCast(asset, SkillCastTarget, EffectEvaluationContext) |
| Runner.Grant(asset, level, overrides) | Grant(asset, level)；动态值在施放前显式解析 |
| TimelinePlayer.TryCast(...) | Runner.TryCast(...) |
| Runner.IsActive / CooldownRemaining / CooldownDuration | runner.GetSpec(asset) 上的同名状态 |
| Runner.Cancel(asset) | spec.Owner.EndAbility(spec) |
| Runner.RevokeAll / AbilitySetAsset.RevokeFrom | Runner.Revoke(asset)，项目管理自己的授权来源 |
| LastActivationCheck / LastCommitCheck / LastCastCheck | 使用本次调用返回的 AbilityActivationCheck |
| CommitAbility(spec) | CommitAbility(spec, capturedActivationVersion) |
| AttributeSet.Remove / Clear / Version / Has | 初始化固定属性表；查询 Get / TryGet；ASC.Clear 保留属性 |
| 公开 AttributeModifier 安装/移除 | 通过 GameplayEffectContainer 管理 modifier 生命周期 |
| DurationAttribute / PeriodAttribute | EffectMagnitude.FromAttribute，通过 durationMagnitude / periodMagnitude 配置 |
| Effects.TryGetRemainingTime | Effects.TryGetInfo 返回的 RemainingTime |
| EffectMagnitude.FromCaller / CallerKey | FromParameter / ParameterId |
| GameplayEffectSpec 的第 4 个字典参数 | EffectEvaluationContext；只有一份不可变参数 |
| AnimationTask 的目标、时长覆写、备用服务参数 | AnimationTask(definition, driver, execution, markerCallback) |
| IAnimationDriver 的名字/Loop/时长参数 | Play(AnimationClip, blendIn)、Advance(normalizedTime)、Stop(blendOut)、Dispose |
| SkillSpawnPosition、HitPoint / LaunchPoint / AimOffset | SkillOrigin，仅保留施放者、目标、目标点和事件位置 |
| SkillCastCallback 四个重复上下文参数 | Action<SkillExecutionContext> CastStarted |
| TargetSearcher.QueryAt / applyLimit 重载 | 一个 Query，中心和朝向为可选参数 |
| ITargetFilterSnapshot.Capture | ITargetFilter 接收 AbilitySourceIdentity，项目明确处理原单位身份 |

`MagnitudeKind.Parameter = 3`、`LevelTable = 4`、`Calculation = 5` 保留原序列化值，旧 SetByCaller 的数值 2 不再有效。Unity 编辑器必须用 `SerializedProperty.intValue` 读写枚举底值，不能把 `enumValueIndex` 当成底值。

## 保留的正确性约束

精简后仍保留激活版本、效果所有权、标签计数、目标 generation、取消期间任务清理和构建期配置校验。这些约束直接决定旧回调是否会影响新施放、新单位；不是重复容错壳。

本轮同时修复：

- 较早 CastStarted 订阅者取消或重施放后，后续时间轴不得挂到旧上下文。
- 过滤回调回收目标后，旧查询不得接受新 generation。
- 切换到无 Transform 的逻辑目标时，清除上一目标坐标。
- Runner 不再按可变覆盖数组身份缓存参数；只缓存不可变 EffectEvaluationContext。

## 升级前检查

这是破坏性重构。旧 SerializeReference 动作、旧动画后端组件、模块/参数集/套组资产，以及旧样例场景都需要迁移；没有兼容别名或自动迁移器。先在独立工程中导入重构分支，清理已删除类型的资产引用，再运行 F7、EditMode、PlayMode 与目标平台构建。

## 验证边界

Core 的 Release 构建、行为回归与托管分配检查在 .NET 8 执行。程序集边界脚本与 Roslyn 语法解析辅助检查删除后的源码。真实 Unity 编译、编辑器交互、Playables 输出、IL2CPP、移动端性能仍需 Unity 工程验证；不把语法解析当成 Unity 编译结果。
