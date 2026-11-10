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

Core 只有 `TryActivateAbility` 和带版本的 `CommitAbility`；Unity 资产施放只有 `SkillRunner.TryCast`。两者不是平行的宿主入口：Runner 负责资产准备和动作执行，内部调用 Core 生命周期。

自动提交先检查标签、消耗与共享冷却。延迟提交在激活时检查可启动条件，实际提交时重新检查资源与冷却。消耗与冷却仍为普通 Effect。提交幂等，同版本重复提交不会支付第二次；旧版本提交返回 Inactive。回调导致能力结束时，返回最终 check，不依赖额外历史字段。

Spec 是状态查询入口。Runner 不再复制 IsActive、CooldownRemaining、CooldownDuration、Cancel 等 API；按资产查询只保留 GetSpec。

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

Realtime 保留一条技能时间轴和一个 UnityEngine.Object 片段引用。主包的 Playables 驱动解释 AnimationClip，可选 Spine 包的驱动解释 AnimationReferenceAsset；公共时间轴不识别后端。Marker、Cue、标签窗口、命中窗口与音效都以定义的秒数推进。AnimationTask 必须接收现有 SkillExecutionContext，不推测目标、创建备用上下文或重写时长。

IAnimationDriver 只有 Play(Object, blendIn)、Advance(normalizedTime)、Stop(blendOut)、Dispose。主包的 Playables 实现负责线性混入混出；Spine 4.3 驱动由可选包提供。Locomotion、角色动画状态机和 RootMotion 策略由项目管理。技能结束和表现资源释放有独立且明确的时机。

驱动定位技能片段，表现宿主推进混合；混出可在任务结束后继续。Spine 的原生事件不驱动 GAS 业务。现有 AnimationTask 先处理边界再定位驱动，Playables 也由其图稍后求值，因此不能把 Marker 中读取到的骨骼当作该边界的同步采样结果。骨骼精确命中需要公共采样与挂点同步约定，不属于当前动画适配范围。

## 准备与热路径

Grant/资产构建可以分配，Player 施放不重新编译资产。持久效果和任务生命周期回归在 Core 验证。选择目标产生的分支上下文、Unity Playable 与对象创建不纳入“Core 热路径 0 B”的断言。最终性能目标需在真实 Unity、IL2CPP 和目标设备上测量。
