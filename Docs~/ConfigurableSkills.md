# 可配置技能工作流

本轮目标是让技能资产承担业务配置。结构图仍展示资产关系；动作由强类型执行器运行，运行期不扫描类型、不解析脚本、不解释图连线。

## 从示例开始

运行 `Tools / ZFramework GAS / 创建 BasicCombat 示例`。生成器负责创建全部示例资产、角色 Prefab、动画和场景。不要手工修改生成文件后把结果当作生成器功能。

技能的 Details 与「能力设置」提供展示信息、参数表和激活动作。动画事件的 Details 提供有序动作列表。动作类型选择器支持排序、复制和删除；复制会生成新的动作 ID。事件名称与动作名称只负责显示。

无动画技能配置「激活动作」即可；动画技能把动作放到需要触发的标记点。程序先 `SkillRunner.Grant` 或通过技能套组授予，再调用 `TryCast`、推进 ASC，并接入项目服务。

## 从现有项目接入

**单位接一次，技能按资产接。** 已有角色组件实现 `IAbilitySystemProvider`，让碰撞体能找到单位的 ASC。初始化时把施法挂点和项目服务交给 `SkillRunner`，用技能套组授予资产。项目自己的战斗循环继续推进 ASC，每单位每步一次。

```csharp
// 单位初始化。MatchesTeam 使用项目自己的阵营表；没有敌友规则可省略。
var runner = new SkillRunner(asc, () => skillAnimator.Driver, new SkillUnitServices {
    Origin = transform, LaunchPoint = muzzle, TargetRelations = MatchesTeam
});
abilitySet.GrantTo(runner);

// 施放时只交输入；以下是三种不同瞄准方式。
runner.TryCast(skill, new SkillCastTarget(targetAsc, aimTransform));
runner.TryCast(skill, SkillCastTarget.Ground(worldPoint));
runner.TryCast(selfBuff);
```

在动作的目标查询中选 **敌方、友方或不限**。例如把圣域的查询设为「圆形、友方、允许自身」，它就能治疗范围内的友方；改成另一种治疗技能不需要给施放入口加分支。BasicCombat 的 Q 只切换待施放的能量弹资产。使用旧重载传入非空 `TimelineTaskContext` 时，该次施放使用传入的整份服务。

**少量技能直接在编辑器配置；已有表格或流程资产才写导入器。** 导入器留在消费项目的 Editor 程序集，引用 `ZFramework.GAS.Editor`。程序将源数据中的时间、属性 ID、标签和资源引用翻译成 GAS 资产。效果、动画片段、Prefab 都可以引用项目中已有的对象，不需要复制进包。

例如把一条「第 12 帧命中，接下来 6 帧播放特效」的数据写到已保存技能的时间轴上。这里的 `skill`、`damage` 是已有资产，Cue 标签由项目注册：

```csharp
var timeline = skill.Animation ?? SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(skill, "主时间轴");
SkillTimelineAuthoring.Edit(timeline, t => {
    const float fps = 30;
    t.Clip("Base Layer.Attack", 1f, fps);
    t.Marker("impact-1", 12 / fps, "命中", new ApplyEffectAction { effect = damage });
    t.Cue("impact-fx", 12 / fps, 18 / fps, "Cue.Attack");
});
SkillAssetFactory.SetMainAnimation(skill, timeline);
AssetDatabase.SaveAssetIfDirty(timeline);
AssetDatabase.SaveAssetIfDirty(skill);
```

时间单位是秒。元素 ID 在同一条时间轴内唯一，使用源数据的稳定键，不要用显示名或数组序号。重复写入同一 ID 会更新该元素，未提及的元素保留；标记内的动作列表则由本次传入的动作替换。编辑器布局和断点依赖动作 ID，若要保留它们，应复用原动作。以导入器为准的字段不要同时交给策划手改；源文件删除的元素由导入器显式处理。`Edit` 在回调成功后一次提交，不自动保存文件。

**玩法按边界分工**：扇形攻击、条件状态、冷却和延时效果直接组合现有资产；实际位移、伤害拦截等需要项目服务的规则留在项目侧。多个技能都用同一动作组合，先提取为共享模块。只有新机制无法组合时才写项目动作，并按需提供几何预演；基础库不依赖项目程序集。

## 参数与等级

参数包括稳定 ID、显示名、说明、类型（Float / Int / Bool）、默认值和可选等级表。等级从 1 开始，超过表长使用末项。绑定时选择参数；改显示名不改变 ID。

解析顺序：共享参数集默认值 → 当前技能覆盖 → 授予时覆盖 → 本次施放覆盖。授予时覆盖属于当前技能在该单位上的配置。每个授予项只保留当前等级和授予配置的默认结果。重复使用同一份施放覆盖数组会复用结果；修改覆盖请换数组，调用方不再持有的一次性数组可回收。已开始的施放仍使用原来的等级和参数。Core 默认实时读取来源属性，显式传入含来源属性的 `EffectEvaluationContext` 才会采集快照。

数值框选择「固定值」或者参数。参数绑定的数值是 `参数 × 系数`。效果数值还可选择等级表、来源／目标属性和受限伤害公式：

```
Clamp(常量 + 来源属性 × 系数 × 参数 - 目标属性 × 目标系数, 最小值, 最大值) × 结果系数
```

生命扣除通常使用结果系数 -1、最小值 0。特殊算法实现 Core 的 `IEffectCalculation`。效果定义仍按引用共享，等级和参数进入 `GameplayEffectSpec.Context`，不为每一级复制效果定义。

编辑器及定义构建阶段检查配置。施放不再遍历动作和子效果做预检；实际的标签、冷却和资源门槛仍在施放时判断。消耗、冷却数值每次提交只求值一次。项目计算和调试回调应自行保证不会抛异常；宿主记录错误并结束能力，不做自动退款。SetByCaller 只在需要保留的效果上快照，保留原字典的键比较器。

## 动作与生命周期

| 动作 | 配置 | 默认生命周期 |
|---|---|---|
| ApplyEffectAction | 效果、目标查询 | 立即执行 |
| ConditionalAction | 标签、属性比较、目标是否存在、两个分支 | 立即执行 |
| TargetSequenceAction / NextTargetStepAction | 有限目标序列；首目标从 0 计跳，后续步骤仅由显式继续动作启动，可放在实际命中出口。总上限包含所有分支，最多 64 个；序号绑定 Int、衰减倍率绑定 Float | 每条链独立；可指定字符串记录键，与本次施放的其他链共享已选单位 |
| SelectTargetsAction | 以选定原点查询，按距离对合格目标执行子动作；排除当前／已访问对象后再截取。可把所选单位写入本次施放的共享记录 | 同步，不改外层目标 |
| ShowTargetLinkAction | 从指定原点到所选目标点显示短暂 LineRenderer，无伤害逻辑 | 场景任务 |
| LaunchProjectileAction | Prefab、速度、寿命、弧线、碰撞、命中动作 | 场景任务 |
| CreateAreaAction | 查询形状、时长、周期、进入／离开效果、周期动作 | 场景任务；引导显式选 Ability |
| SpawnPrefabAction | Prefab、位置、朝向、挂点、跟随、寿命 | 场景任务 |
| SendGameplayEventAction | 逻辑事件标签、接收目标、类型化负载 | 立即执行 |
| CallModuleAction | 模块资产、模块参数到调用方参数的绑定 | 继承内部动作 |
| WaitSkillAction | 延时／等待事件／等待选目标，成功／超时／取消动作 | 技能任务 |

选择目标、发射投射物、生成预制体都可选择施法者、当前目标、目标点、上一命中点或事件位置作为原点；这不会改写攻击来源。「发送 Gameplay 事件」需要勾选携带位置，接收方才能用事件位置。「目标序列」在启动每一步前预留所选单位，避免同一帧两个分支抢到同一个目标；超出范围、没有下一目标或达到上限时结束。若需要只共享**已实际命中**的单位，可在投射物的命中分支用「选择目标」选择当前固定目标、关闭排除当前／已访问、勾选记录所选单位，再在后续范围查询使用同一个记录键。记录只在本次施放中共享，修改键时须同时修改各个引用。

技能动画、等待和显式选择 Ability 的区域随取消结束。正常播放结束后，技能等待这些所属任务完成。已经发出的投射物、区域和召唤物在场景宿主中独立调度，不占 ASC 的任务列表，也不阻止再次施放。

`SceneSkillTaskHost` 随场景卸载清理。项目可在 `TimelineTaskContext.SceneTasks` 注入手动调度宿主（适合测试或已有世界时钟），此时由项目负责 Tick 和 Clear。

新建技能时，**持续伤害（打断后继续）** 会在标记触发时把周期效果挂到目标，此后由目标的 ASC 结算；**引导（随施放取消）** 会在标记触发时启动技能所属的周期任务，每半秒对固定目标施加一次瞬时伤害。打断前都能造成伤害，打断后只有前者继续。要修改每跳间隔，前者改效果的周期，后者改区域动作的周期。

`ISkillObjectService` 接管生成与归还，默认实现为 Instantiate / Destroy。池实现必须在 Spawn 时重置对象，Release 时归还同一个对象。Prefab 可实现 `ISkillSpawnInitializer` 接收初始化上下文；AI、导航和阵营属于项目代码。

### 新动作应放在哪里

能用已有动作拼出的技能，保存成技能资产或共享模块。需要新玩法时，把动作和任务写在项目程序集中；只有多个项目都要遵守的规则才放回 GAS。下面是项目动作接入现有工具的三个入口：

- **编辑与构建**：派生 `SkillAction` 并实现 `Build`。用 `[SerializeReference, FlowBranch("抵达")]` 声明子动作数组，构建时调用 `BuildBranch(builder, nameof(afterTravel), afterTravel)`。分支默认继承调用方的约束；若它可能晚于施放结束，重写 `IsDetachedBranch`。构建器、编辑校验、流程图使用同一条规则。动作类上的 `[System.ComponentModel.DisplayName("延迟落点")]` 同时用于菜单和节点；`[FlowField("飞行时长")]` 可将常用字段放在节点上，其余字段在 Details 编辑。动作内对 GAS 效果和时间轴的引用会出现在结构图上，字段可以自行命名。
- **运行时**：用 `context.Start(task, SkillTaskOwnership.Scene)` 发出可独立完成的任务，用 `Ability` 启动需要随施放取消的任务。目标、命中位置、事件和局部参数属于当前分支；命中记录和表现通道属于整次施放。项目任务派生 `SkillActionTask` 时使用它的 `Execution`，不用把这些状态复制到资产里。一次取消中创建的场景任务仍属于原来的施放。
- **几何预演**：项目需要展示独有的几何行为时，在项目 **Editor** 程序集里用 `[InitializeOnLoadMethod]` 调用 `SkillActionPreview.Register<T>`。处理器是同步的：`frame.Mark(query)` 按当前查询标出假人，`frame.Visit(children, delay)` 将分支放在延迟后的时间点，传入地面点则仅在该分支清除单位目标并改用该点。只描述能确定的部分；没有处理器的自定义动作不会在编辑器里执行游戏代码。

以「抵达后等待一次格挡」为例：项目的位移任务在抵达时执行配置的子动作。策划在其中放一个「等待 Gameplay 事件」：成功分支接反击效果，超时分支接普通伤害。项目的格挡判定只需向施法者的 `asc.Events` 发送约定标签，无需读取技能资产或挑选伤害效果。因为「等待」随技能取消，承载这个分支的任务也要选择技能归属；独立投射物不能在命中后启动这种等待。

## 选敌

查询支持自身、固定目标、目标点周围、圆形、扇形和线形。可配置 LayerMask、任一／必需／阻断标签、排除自身和最多目标数；按单位去重并按距离排序截取。预演和运行时使用同一份原点、形状尺寸、扇形角度判定和标签规则；假人用球形代理，场景用 Unity 碰撞体。没有注册的标签不会成为有效的查询条件。

物理查询读取 Unity 当前物理世界，不会自行调用 `Physics.SyncTransforms`。嵌套查询分别租用结果缓冲，外层结果不会被覆盖。默认缓冲为 256 个碰撞体。即时多目标动作在开发构建中断言缓冲未饱和；项目应限制碰撞层和范围。持续区域不会根据不完整的结果移除占用者。

项目在 `SkillUnitServices.TargetRelations` 提供 `bool MatchesTeam(AbilitySourceIdentity source, AbilitySystemComponent target, TargetRelation relation)`。「不限」不查阵营；未接入服务时「友方／敌方」均不接纳目标。关系判断同样用于固定目标、范围查询和投射物命中；标签与已有的 `ITargetFilter` 仍然生效。关系服务拿到的是原施法者的代际标识；若投射物在单位被回收后仍需判敌，可按该标识保存队伍。用活动单位查队伍的项目遇到未知来源应返回 false。

目标选择交互通过 `ISkillTargetSelection` 提供。目标点优先用本次施放传入的点（包括世界原点），否则取施放时的瞄准 Transform 位置；两者都缺失时，依赖目标点的动作会报错。参数作用域和事件分支保留目标点；`WithTarget` 提供新的 Transform 时更新它。模块在调用它的参数作用域内复用绑定，每次调用使用当时的目标和位置。等待任务保留创建时的参数和位置。

如果还接入了依赖施法者的旧 `ITargetFilter`，可通过 `ITargetFilterSnapshot.Capture` 冻结过滤所需数据。普通 `ITargetFilter` 在来源失效后不再被调用，该查询不接纳目标。

## 共享与变体

用独立 `SkillActionModuleAsset` 保存共享动作，用 `SkillParameterSetAsset` 保存共享参数。模块可以在带标签页的独立编辑窗口中打开；窗口提供引用范围入口。修改模块会影响引用者；技能私有配置不会反向传播。

变体是独立技能资产，引用同一模块，仅覆盖参数和展示信息。模块和条件嵌套最多八层，循环引用在构建时拒绝。`AbilitySetAsset` 一次授予多项技能、初始等级和参数覆盖。

## 运行时观察

打开 `Window / ZFramework GAS / 运行时调试`，观察单位的技能、时间轴、标签、属性和效果，并查看最近失败、解析后的参数与动作／场景任务状态。点击动作或失败项可跳到配置位置。

技能编辑器的「跟随运行时」只在匹配正在运行的动画任务时驱动播放头；没有匹配任务时仍可本地预览。联动只改播放模型，不写资产或 Undo。

## Section、曲线与动画后端

动画 Section 通过稳定 ID 引用下一段。`SkillRunner.QueueSection(asset, sectionId)` 仅在当前段输入缓冲窗口内接受输入。相邻段自然衔接保持跨段的 Cue、标签和音效，不重新启动它们；段采用 `[start, end)`：段终点的标记、Cue 与新轨道只在自然连续进入下一段时处理一次；循环到自身、跳至不相邻段或直接结束时不触发终点事件。显式或非连续跳段会先关闭原段状态，不补发被跳过的标记；到达的新位置重新开启覆盖该位置的窗口。边界、Section 位置和输入窗口使用相同的时长换算。

扩展轨道实现 `SkillRuntimeTrack.Build()`、`RuntimeTrackDefinition.Create()` 与 `IRuntimeTrackHandler` 的 Enter / Sample / Exit / Cancel。曲线轨写入本次 `SkillExecutionContext.Channels`，不会修改共享属性基础值。

可选 Playables 后端独立控制技能片段的速度，基础 Animator Controller 继续运行。LayerMixer 可配置 AvatarMask 与叠加层。现有 Animator、Legacy、序列帧等后端保留。运行期根运动通过 `ISkillMovementAdapter` 交给项目角色移动逻辑。

时间轴「＋轨道 / 动画段」创建 Section，Details 的下一段下拉显示名称但保存稳定 ID。曲线／扩展轨道的类型菜单可选择 `AudioSkillTrack`：拖入 AudioClip，设置区间、音量、空间化与循环；退出或取消轨道时停止声音。项目可通过 `ISkillAudioService` 替换默认 AudioSource。

预览上方可打开范围、假人、前后帧、根位移和音效。范围直接读取动作查询及模块参数；等级和目标距离只影响预览。假人用静止的球形代理预演命中；分叉和弹射可显示逐段路线。预演与运行时使用相同的跳数、衰减及抛物线高度公式，不执行项目的阵营判断和移动追踪；最终命中以场景物理结果为准。前后帧显示当前时间前后各三帧；根位移显示片段采样的轨迹，原地动画或已烘焙到姿态的根运动可能没有位移。音效使用窗口独立持有的 AudioSource 通道，重叠音轨可以同时播放；退出区间只停止对应通道，关闭窗口只释放自身资源。暂停立即停止试听，恢复从当前播放头继续。时间轴按真实编辑器时间推进，慢帧不再截成 0.1 秒；拖动或循环回到起点才显式跳转。预览为居中试听，不模拟运行期空间音频混音。

## 不写业务回调的五个配置练习

先复制生成的技能到自己的目录，再修改配置；重新运行示例生成器会覆盖示例目录。

1. **扇形群攻**：打开斩击动画的「命中」标记，进入引用的扇形斩击模块。ApplyEffectAction 选择伤害效果，TargetQuery 选 Sector；绑定半径参数，设置夹角、LayerMask 和最大目标数。选择 Health 修改器的 Calculation，来源 Attack、目标 Armor、结果系数 -1、最小值 0。
2. **条件强化**：ConditionalAction 的 requiredTags 设 State.Empowered，whenTrue 放强化伤害，whenFalse 放普通伤害。战吼的增益效果授予 State.Empowered；先按 3 再按 1 应看到伤害变高。改标签显示文本无效，条件需要实际注册标签。
3. **延迟命中**：能量弹的「发射」标记使用 LaunchProjectileAction；选择外观 Prefab、速度、寿命及碰撞层，onHit 放固定目标的 ApplyEffectAction。Ownership 保持 Scene。把速度改小，动画结束后调试窗口仍能看到场景任务，命中时才扣生命。
4. **持续区域与引导**：召唤中的 CreateAreaAction 选择 TargetPoint／Circle，配置 duration、period 和 onPeriod；默认 Scene。圣域配置 Ability，enterEffect 是周期治疗，removeOnExit 开启；取消技能应立即移除区域效果。Ability 区域 duration=0 表示持续到取消，Scene 区域必须有正寿命。
5. **定时召唤**：SpawnPrefabAction 选择 Guardian Prefab，设置位置／偏移／朝向、数量、间距及寿命；默认 Scene。如需没有动画的延迟召唤，在能力激活动作中添加 WaitSkillAction（Delay），onSuccess 放生成动作；打断等待不会召唤，已经生成的对象继续存活到寿命结束。

改变等级：在 BasicCombatAbilities.asset 对应项填写 level，再进入 Play。改变单个技能数值：在技能的 parameterOverrides 选参数填写覆盖。模块绑定的来源参数按 ID 读取；只改参数显示名、动作名或标记名不会影响执行。删除参数、Prefab、效果或模块后，使用编辑器消息面板定位失效的引用，再重新构建定义。

## 生产工具与安全暂停

Window / ZFramework GAS 下提供「技能批量配置」「查找引用」「技能语义 Diff」。参数化冷却的批量修改写入当前技能覆盖；共享固定效果的编辑影响全部引用者。语义 Diff 先保存 JSON 快照，再与当前技能比较，动作按稳定 ID 对齐、排序单列、布局单独展示。

结构图的图工具支持对齐、等距、折叠和隐藏空端口；书签保存视图。双击连线可加编辑器重路由点，删除点恢复原连线，不改变技能关系。普通字段编辑按依赖失效，技能库增量处理资产导入／移动／删除；大量技能时列表虚拟化。

运行时调试可以请求在下一动作边界暂停，或对动作设置暂停点。Unity 会在当前世界帧结束时暂停，同一同步动作批次会完成；这是整个场景暂停与逐帧推进，不是可挂起任意 C# 调用栈的逐指令调试。逐帧时物理、动画和所有单位一起推进。

## 宿主接入与验证

先显式授予，施放前无需再调用 CheckActivation；该方法留给只读显示。套组仅撤销本来源的授予，重复授予以最近来源为准。场景卸载由场景任务宿主清理。Core 与 Unity 的效果来源均以 `AbilitySourceIdentity` 区分单位回收前后的身份。

配置错误由构建器和编辑器报告；运行时只处理当次施放的玩法门槛。任务异常直接交给宿主，在边界记日志并结束能力。每个 ASC 和场景宿主每帧只 Tick 一次，不在回调里嵌套 Tick。

不得在动画回调中施放或修改技能驱动。技能层权重归零后由 Controller 自然接管；项目负责未绑定挂点的复位。提取和内联模块不可撤销；跨文件粘贴或内联时必须先外置私有效果。详见[编辑器指南](EditorGuide.md)。
