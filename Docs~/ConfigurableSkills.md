# 可配置技能工作流

本轮目标是让技能资产承担业务配置。结构图仍展示资产关系；动作由强类型执行器运行，运行期不扫描类型、不解析脚本、不解释图连线。

## 从示例开始

运行 `Tools / ZFramework GAS / 创建 BasicCombat 示例`。生成器负责创建全部示例资产、角色 Prefab、动画和场景。不要手工修改生成文件后把结果当作生成器功能。

技能的 Details 与「能力设置」提供展示信息、参数表和激活动作。动画事件的 Details 提供有序动作列表。动作类型选择器支持排序、复制和删除；复制会生成新的动作 ID。事件名称与动作名称只负责显示。

无动画技能配置「激活动作」即可；动画技能把动作放到需要触发的标记点。程序只需要调用 `SkillRunner.TryCast`、推进 ASC，并接入项目服务。

## 参数与等级

参数包括稳定 ID、显示名、说明、类型（Float / Int / Bool）、默认值和可选等级表。等级从 1 开始，超过表长使用末项。绑定时选择参数；改显示名不改变 ID。

解析顺序：共享参数集默认值 → 当前技能覆盖 → 授予时覆盖 → 本次施放覆盖。授予时覆盖属于当前技能在该单位上的配置。施放开始后冻结等级、参数和来源属性；执行中的投射物不读取后来升级的数值。

数值框选择「固定值」或者参数。参数绑定的数值是 `参数 × 系数`。效果数值还可选择等级表、来源／目标属性和受限伤害公式：

```
Clamp(常量 + 来源属性 × 系数 × 参数 - 目标属性 × 目标系数, 最小值, 最大值) × 结果系数
```

生命扣除通常使用结果系数 -1、最小值 0。特殊算法实现 Core 的 `IEffectCalculation`。效果定义仍按引用共享，等级和参数进入 `GameplayEffectSpec.Context`，不为每一级复制效果定义。

消耗检查、消耗提交、冷却、标记效果、命中、周期及子效果沿用同一上下文。Core 与 Unity 动作入口共用效果树预检：消耗的全部操作、冷却的修改器、持续时间／周期及递归子效果均在打断其他技能和扣费前解析。瞬时效果先解析全部修改器，再写入属性；同一效果的公式读取写入前的目标状态。该保障不回滚项目事件回调自身的副作用。已提交的消耗与冷却在后续动作失败时不退款。

Int 参数必须是 Int32 范围内的整数，Bool 的数值配置只接受 0／1；不会静默截断小数或把任意非零数转为 true。效果数值、持续时间及周期拒绝 NaN／Infinity。SetByCaller 在通过应用标签门禁后，仅对持续效果或带子效果的效果复制字典，并保留原字典的键比较器；周期与子效果共享快照。普通瞬时命中直接读取当次字典，预热后不为快照分配内存。

## 动作与生命周期

| 动作 | 配置 | 默认生命周期 |
|---|---|---|
| ApplyEffectAction | 效果、目标查询 | 立即执行 |
| ConditionalAction | 标签、属性比较、目标是否存在、两个分支 | 立即执行 |
| LaunchProjectileAction | Prefab、速度、寿命、弧线、碰撞、命中动作 | 场景任务 |
| CreateAreaAction | 查询形状、时长、周期、进入／离开效果、周期动作 | 场景任务；引导显式选 Ability |
| SpawnPrefabAction | Prefab、位置、朝向、挂点、跟随、寿命 | 场景任务 |
| SendGameplayEventAction | 逻辑事件标签、接收目标、类型化负载 | 立即执行 |
| CallModuleAction | 模块资产、模块参数到调用方参数的绑定 | 继承内部动作 |
| WaitSkillAction | 延时／等待事件／等待选目标，成功／超时／取消动作 | 技能任务 |

技能动画、等待和显式选择 Ability 的区域随取消结束。正常播放结束后，技能等待这些所属任务完成。已经发出的投射物、区域和召唤物在场景宿主中独立调度，不占 ASC 的任务列表，也不阻止再次施放。

`SceneSkillTaskHost` 随场景卸载清理。项目可在 `TimelineTaskContext.SceneTasks` 注入手动调度宿主（适合测试或已有世界时钟），此时由项目负责 Tick 和 Clear。

独立投射物的命中、独立区域的周期回调，以及等待任务的取消分支，不能启动 Wait 或其他 Ability 归属的任务；模块内部也遵循同一限制，构建时提供动作定位。可使用同步效果／事件或 Scene 归属对象。生成服务与初始化回调中正常取消施法，会归还尚未托管的对象并停止后续生成，不记为执行失败。取消分支的运行时错误记录在 `Spec.LastCleanupError`／`LastCleanupActionId`，调试窗口显示“取消清理警告”并可定位动作；不从 `EndAbility` 抛出，也不取消此前已经发出的场景任务。该清理分支自己创建的资源仍会在分支失败时回收。

旧的 ProjectileSpawner／AreaEffectSpawner 保留技能归属；AreaEffectSpawner 的 maxOccupants 仍是旧查询缓冲容量。新 CreateAreaAction 的 maxTargets 是稳定的占用人数上限：已进入的单位保留到实际离开，空位按距离补入，不因近远排名变化反复重施加进入效果。

`ISkillObjectService` 接管生成与归还，默认实现为 Instantiate / Destroy。池实现必须在 Spawn 时重置对象，Release 时归还同一个对象。Prefab 可实现 `ISkillSpawnInitializer` 接收初始化上下文；AI、导航和阵营属于项目代码。

若调用方尚在使用 TimelineTaskContext.RentVisual／ReturnVisual，且没有设置 Objects，统一执行上下文会将这一对旧回调适配成对象生成服务，不绕过已有对象池。

## 选敌

查询支持自身、固定目标、目标点周围、圆形、扇形和线形。使用 LayerMask、必需／阻断标签、排除自身和最多目标数；按单位去重并按距离排序截取。

物理查询读取 Unity 当前物理世界，不会自行调用 `Physics.SyncTransforms`。嵌套查询分别租用结果缓冲，外层结果不会被覆盖。默认缓冲为 256 个碰撞体；饱和时即时多目标动作会报错，持续区域不根据不完整结果错误移除占用者。

项目通过 `ITargetFilter` 接入阵营过滤，不在 GAS 中规定玩家、怪物等分类。目标选择交互通过 `ISkillTargetSelection` 提供，并返回可取消的请求句柄。目标点来自显式设置的 `TargetPosition`，或者施放开始时捕获的目标 Transform 位置；两者都缺失时拒绝依赖目标点的动作，显式指定世界原点是有效配置。参数作用域和事件分支保留目标点；只有 `WithTarget` 明确重绑目标时才更新它。模块只缓存参数绑定，每次调用刷新空间数据；等待任务捕获创建时的数据，不被之后的模块调用覆盖。

会回收施法者的项目应额外实现 `ITargetFilterSnapshot.Capture`，冻结本次施放的阵营数据；独立任务不会将回收后的 ASC 当作旧来源。普通 `ITargetFilter` 在来源失效后不再被调用，该查询不接纳目标；需要继续选敌时必须提供快照过滤器，避免向依赖来源对象的项目代码传 null。

## 共享与变体

用独立 `SkillActionModuleAsset` 保存共享动作，用 `SkillParameterSetAsset` 保存共享参数。模块可以在带标签页的独立编辑窗口中打开；窗口提供引用范围入口。修改模块会影响引用者；技能私有配置不会反向传播。

变体是独立技能资产，引用同一模块，仅覆盖参数和展示信息。模块和条件嵌套最多八层，循环引用在构建时拒绝。`AbilitySetAsset` 一次授予多项技能、初始等级和参数覆盖。

显式重复 `SkillRunner.Grant` 会更新下一次施放的等级和授予参数，运行中的快照保持不变。TryCast 仅在尚未授予时以 1 级隐式授予。套组按来源记录授予贡献，同一技能有多个来源时最近一次显式授予生效；撤销该来源后恢复剩余来源中最近的一份，最后一个来源撤销时才收回技能。参数覆盖的等级表会复制，后续修改调用方数组不改变已授予配置。

## 运行时观察

打开 `Window / ZFramework GAS / 运行时调试`，观察单位的技能、时间轴、标签、属性和效果，并查看最近失败、解析后的参数与动作／场景任务状态。点击动作或失败项可跳到配置位置。

技能编辑器的「跟随运行时」只在匹配正在运行的动画任务时驱动播放头；没有匹配任务时仍可本地预览。联动只改播放模型，不写资产或 Undo。

## Section、曲线与动画后端

动画 Section 通过稳定 ID 引用下一段。`SkillRunner.QueueSection(asset, sectionId)` 仅在当前段输入缓冲窗口内接受输入。相邻段自然衔接保持跨段的 Cue、标签和音效，不重新启动它们；段采用 `[start, end)`：段终点的标记、Cue 与新轨道只在自然连续进入下一段时处理一次；循环到自身、跳至不相邻段或直接结束时不触发终点事件。显式或非连续跳段会先关闭原段状态，不补发被跳过的标记；到达的新位置重新开启覆盖该位置的窗口。边界、Section 位置和输入窗口使用相同的时长换算。

扩展轨道实现 `SkillRuntimeTrack.Build()`、`RuntimeTrackDefinition.Create()` 与 `IRuntimeTrackHandler` 的 Enter / Sample / Exit / Cancel。曲线轨写入本次 `SkillExecutionContext.Channels`，不会修改共享属性基础值。

可选 Playables 后端独立控制技能片段的速度，基础 Animator Controller 继续运行。LayerMixer 可配置 AvatarMask 与叠加层。现有 Animator、Legacy、序列帧等后端保留。运行期根运动通过 `ISkillMovementAdapter` 交给项目角色移动逻辑。

Playable 混合中再次 Play 会保留当前混合权重；Stop 立即停止向技能移动适配器提交根运动并恢复原设置，混出仍可继续。Seek 仅采样姿势，不提交根运动。Unity 6000.3.14f1 中已验证普通 Animator.SetFloat／SetTrigger 可操作实际控制器，并验证接管前的当前状态与播放进度；项目自定义 StateMachineBehaviour、复杂过渡及其他 Unity 版本仍应进行接入验证。

时间轴「＋轨道 / 动画段」创建 Section，Details 的下一段下拉显示名称但保存稳定 ID。曲线／扩展轨道的类型菜单可选择 `AudioSkillTrack`：拖入 AudioClip，设置区间、音量、空间化与循环；退出或取消轨道时停止声音。项目可通过 `ISkillAudioService` 替换默认 AudioSource。

预览上方可打开范围、假人、前后帧、根位移和音效。范围直接读取动作查询及模块参数；等级和目标距离只影响预览。假人是尺寸参照线框，不执行伤害与阵营过滤；最终命中以物理碰撞体为准。前后帧显示当前时间前后各三帧；根位移显示片段采样的轨迹，原地动画或已烘焙到姿态的根运动可能没有位移。音效使用窗口独立持有的 AudioSource 通道，重叠音轨可以同时播放；退出区间只停止对应通道，关闭窗口只释放自身资源。拖动或循环回到起点会显式跳转，普通慢帧不会重启声音。预览为居中试听，不模拟运行期空间音频混音。

## 不写业务回调的五个配置练习

先复制生成的技能到自己的目录，再修改配置；重新运行示例生成器会覆盖示例目录。

1. **扇形群攻**：打开斩击动画的「命中」标记，进入引用的扇形斩击模块。ApplyEffectAction 选择伤害效果，TargetQuery 选 Sector；绑定半径参数，设置夹角、LayerMask 和最大目标数。选择 Health 修改器的 Calculation，来源 Attack、目标 Armor、结果系数 -1、最小值 0。
2. **条件强化**：ConditionalAction 的 requiredTags 设 State.Empowered，whenTrue 放强化伤害，whenFalse 放普通伤害。战吼的增益效果授予 State.Empowered；先按 3 再按 1 应看到伤害变高。改标签显示文本无效，条件需要实际注册标签。
3. **延迟命中**：能量弹的「发射」标记使用 LaunchProjectileAction；选择外观 Prefab、速度、寿命及碰撞层，onHit 放固定目标的 ApplyEffectAction。Ownership 保持 Scene。把速度改小，动画结束后调试窗口仍能看到场景任务，命中时才扣生命。
4. **持续区域与引导**：召唤中的 CreateAreaAction 选择 TargetPoint／Circle，配置 duration、period 和 onPeriod；默认 Scene。圣域配置 Ability，enterEffect 是周期治疗，removeOnExit 开启；取消技能应立即移除区域效果。Ability 区域 duration=0 表示持续到取消，Scene 区域必须有正寿命。
5. **定时召唤**：SpawnPrefabAction 选择 Guardian Prefab，设置位置／偏移／朝向、数量、间距及寿命；默认 Scene。如需没有动画的延迟召唤，在能力激活动作中添加 WaitSkillAction（Delay），onSuccess 放生成动作；打断等待不会召唤，已经生成的对象继续存活到寿命结束。

改变等级：在 BasicCombatAbilities.asset 对应项填写 level，再进入 Play。改变单个技能数值：在技能的 parameterOverrides 选参数填写覆盖。模块绑定的来源参数按 ID 读取；只改参数显示名、动作名或标记名不会影响执行。删除参数、Prefab、效果或模块后，消息面板与运行时失败信息提供配置定位。

## 生产工具与安全暂停

Window / ZFramework GAS 下提供「技能批量配置」「查找引用」「技能语义 Diff」。参数化冷却的批量修改写入当前技能覆盖；共享固定效果的编辑影响全部引用者。语义 Diff 先保存 JSON 快照，再与当前技能比较，动作按稳定 ID 对齐、排序单列、布局单独展示。

结构图的图工具支持对齐、等距、折叠和隐藏空端口；书签保存视图。双击连线可加编辑器重路由点，删除点恢复原连线，不改变技能关系。普通字段编辑按依赖失效，技能库增量处理资产导入／移动／删除；大量技能时列表虚拟化。

运行时调试可以请求在下一动作边界暂停，或对动作设置暂停点。Unity 会在当前世界帧结束时暂停，同一同步动作批次会完成；这是整个场景暂停与逐帧推进，不是可挂起任意 C# 调用栈的逐指令调试。逐帧时物理、动画和所有单位一起推进。

## 验证原则

Core 的旧零分配断言继续保留。施放构建和编辑器刷新允许分配，应单独测量；不能把 Core 零分配结果描述为整套技能系统零 GC。

自动化检查不能替代独立策划验收。最终需由未参与开发的人仅按本文配置扇形群攻、条件攻击、投射物、区域和定时召唤，记录阻碍与歧义。


## 授予来源与 Source 身份

`TryCast` 首次使用的隐式授予是临时来源，之后显式 `Grant` 或套组授予会替代它。`Revoke(asset)` 只撤销直接／临时授予，不撤销套组的贡献；套组通过 `RevokeFrom` 撤销自身贡献。需要彻底删除所有来源时显式调用 `RevokeAll(asset)`。剩余来源恢复最近一次授予的等级和参数。

Unity 动作及直接动画／投射物／区域任务发出的效果和 Cue，`Source` 可以是 `AbilitySourceIdentity`，不再保证是 ASC。直接写 `info.Source as AbilitySystemComponent` 可能得到 null。接入代码应兼容身份对象与调用方自行传入的来源：

```csharp
AbilitySystemComponent ResolveSource(object source) => source switch
{
    AbilitySourceIdentity identity => identity.Owner,
    AbilitySystemComponent asc => asc,
    _ => null
};
```

`Owner` 在原 ASC 调用 `Clear()`、被回收复用后返回 null，避免把旧投射物归属给新单位。不要把 Source 改回裸 ASC；同一实例的新旧 incarnation 需要不同的来源身份。独立任务继续用捕获的等级、参数和来源属性求值，无须从回收后的 Owner 取值。Core 的低层 `GameplayEffectSpec(source: ...)` 仍保留调用方提供的来源对象。
