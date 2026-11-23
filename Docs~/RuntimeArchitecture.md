# 运行时架构与所有权

## 边界规则

库保留所有项目都会遇到的能力生命周期、属性/标签/效果机制和创作工具。投射物、弹射规则、连招、装备来源栈、阵营、相机、顿帧、移动与具体伤害公式由项目负责。新增功能先判断是否能以 `SkillAction`、`GameplayAbilityTask`、`IEffectCalculation` 或项目服务实现；无需为它新增基础层状态。

## 唯一所有者

| 数据 | 所有者 | 结束时机 |
| --- | --- | --- |
| GameplayAbilityDefinition / GameplayEffectDefinition | 缓存它的资产或调用方 | 不修改运行中的定义；资产编辑产生新定义 |
| GameplayAbilitySpec | AbilitySystemComponent | RemoveAbility / Clear 撤销 |
| 激活版本、激活标签、提交状态 | GameplayAbilitySpec | EndAbility 结束当前版本 |
| 效果句柄、堆叠、持续 modifier | GameplayEffectContainer | 到期、移除、Clear |
| SkillExecutionContext | 一次施放及其仍存活的任务 | 引用租约归零后可供同参数施放复用 |
| 查询缓冲 | 施放上下文 | 同步查询 finally 归还 |
| 单位服务 | 项目 | 创建 Runner 时显式注入 |
| 独立项目任务 | 项目持有的 GameplayTaskHost | 项目 Tick / Clear |
| 技能动画图 | PlayableAnimationDriver | SkillTimelinePlayer.Dispose |

`AbilitySourceIdentity` 保存原 ASC 的代际身份。池化单位 Clear 后，旧身份不能解析到新的单位。候选目标在项目过滤回调之前捕获 generation，回调后变化即丢弃；执行过程中仍逐个核验缓冲身份。

## 激活与提交

Core 提供 `CheckActivation`、`TryActivateAbility`、带版本的 `CommitAbility`、`EndAbility` 与 `CancelAbility`，以及便捷效果施加入口 `ApplyEffectToSelf` / `ApplyEffectToTarget`（统一经 `GameplayEffectContainer.Apply` 结算）；Unity 资产施放提供 `SkillRunner.CanCast` 预检与 `SkillRunner.TryCast` 唯一执行入口。两者不是平行的宿主入口：Runner 负责资产准备和动作执行，内部调用 Core 生命周期。

自动提交先检查标签、净值消耗与共享冷却。延迟提交在激活时检查可启动条件，实际提交时重新检查资源与冷却。消耗与冷却仍为普通 Effect；`GameplayAbilitySpec.CooldownRemaining` / `CooldownDuration` 支持跨定义共享冷却标签（GCD / 技能组冷却）。提交幂等，同版本重复提交不会支付第二次；旧版本提交返回 Inactive。回调导致能力结束时，返回最终 check，不依赖额外历史字段。

Spec 是状态查询入口；`ASC.OnAbilityActivated/Committed/Ended` 与 `Effects.OnEffectAdded/Removed/StackChanged` 提供零侵入运行期事件钩子。Runner 不复制 IsActive、CooldownRemaining、CooldownDuration、Cancel 等状态读取 API；按资产查询保留 `GetSpec` 与预检 `CanCast`。

## 参数

技能资产直接声明参数、类型、默认值和等级表。`ResolveParameters` 在冷路径解析为 `SkillParameterValues`；`EffectEvaluationContext` 保存等级、不可变参数和可选来源属性快照。Runner 为默认值与显式上下文缓存执行状态，缓存键是不可变上下文对象。

不再存在共享参数集、模块局部参数、调用绑定、父级参数链、授权来源覆盖栈或第二套 SetByCaller 字典。外部系统需要从表格生成参数时，在项目的数据导入阶段生成同一份资产或上下文。

## 动作与任务

动作定义只表达同步工作与显式异步续体。保留六种内置动作；复杂玩法写成项目 `SkillAction`。等待成功、超时与取消的分支仍保留。取消分支可做同步清理或启动项目独立任务，不能把工作挂回已结束的能力。

能力任务由 ASC 取消，独立任务使用显式注入的 GameplayTaskHost。库不创建隐藏 GameObject、不选择全局 Tick 时机。上下文 Retain/Release 与激活版本检查用于真实异步所有权，不以 catch-and-ignore 掩盖生命周期错误。

## 目标查询

基础层提供自身、指定逻辑目标和项目目标源；空间查询由注入的 TargetSearcher 适配。Combat3D 提供 PhysicsScene 查询和武器碰撞体槽位。每个查询使用自己的缓冲，嵌套分支不会覆盖外层结果。

SelectTargets 只负责按查询结果执行分支。目标上限来自 TargetQuery，不再维护弹射跳数、重复命中预算或每跳衰减。项目的 ITargetFilter 接收原 AbilitySourceIdentity；阵营与失效来源的处理由该服务明确决定。替换为无 Transform 的逻辑目标会清除上一目标的空间坐标。

## 表现

Realtime 保留一条技能时间轴和一个 UnityEngine.Object 片段引用。主包的 Playables 驱动解释 AnimationClip，可选 Spine 包的驱动解释 AnimationReferenceAsset；公共时间轴不识别后端。Marker、Cue、标签窗口、命中窗口与音效都以定义的秒数推进，并支持通过 `SkillExecutionContext.TimeScale` / `AnimationTask.TimeScale` 实现攻速、顿帧（0）与慢动作。AnimationTask 必须接收现有 SkillExecutionContext，不推测目标、创建备用上下文或重写时长。

IAnimationDriver 只有 Play(Object, blendIn)、Advance(normalizedTime)、Stop(blendOut)、Dispose。主包的 `PlayableAnimationDriver` 负责线性混入混出、可选 `AvatarMask` 上下半身遮罩、FootIK、图层排序与空闲暂停；Spine 4.3 驱动由可选包提供。Locomotion、角色动画状态机和 RootMotion 策略由项目管理。技能结束和表现资源释放有独立且明确的时机。

`AnimationTask` 在构造时将所有边界合并排序，同时间点按 `TagOpen < CueOpen < TrackEnter < Marker < TrackExit < TagClose < CueClose` 确定性执行；进入轨道与触发 Marker 前先调用 `IAnimationDriver.Advance` 同步到该边界时刻，单帧跨过整个轨道区间时在退出前于起点补采一次以防命中窗口穿透。若需在武器碰撞体/骨骼挂点命中时同步求值动画姿势，可在构造 `PlayableAnimationDriver` 时开启 `syncPoseOnAdvance: true` 或按需调用 `SyncPose()`。Spine 的原生事件不驱动 GAS 业务。

## 准备与热路径

Grant/资产构建可以分配，Player 施放不重新编译资产。持久效果和任务生命周期回归在 Core 验证。选择目标产生的分支上下文、Unity Playable 与对象创建不纳入“Core 热路径 0 B”的断言。最终性能目标需在真实 Unity、IL2CPP 和目标设备上测量。
