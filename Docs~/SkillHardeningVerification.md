# 技能边界与生命周期修复记录

日期：2026-11-02。此次修复针对 Section、Playable、动作取消、场景任务和参数快照的审查问题；不增加迁移功能，不手工修改生成示例。

以下各轮保留当时的结果。当前行为见文末第五轮：Core 及其直接启动的旧任务默认实时读来源属性，可以显式冻结；SkillRunner 始终冻结。共享起点不再报终点警告，Playable 使用独立输出叠加到项目控制器。

## 修复与行为约定

| 问题 | 修复 |
|---|---|
| Section 共享边界重复／浮点跳过 | 边界、段起止和输入窗口统一使用 `Min(authored × scale, Duration)`；自然衔接保留边界游标；同位置显式跳转保留已消费边界，未触发的落点事件仍可触发。段采用 `[start,end)`，先决定后继；自循环、非连续跳转和结束不消费终点的新事件／音轨 |
| Cue 重复 Removed | 仅在该区间确实开启时派发 Removed；跳转关闭过的区间不会再从关闭边界重复派发 |
| 相邻段重开 Cue／标签／音效 | 连续段只切换当前段身份，保留跨段状态；显式跳转和不连续跳转仍执行旧状态关闭与落点恢复 |
| Stop 后仍转发根运动／Seek 重复移动 | 每次取得根运动归属时捕获项目当前设置，Stop 立即清除移动适配器转发并恢复该设置；Seek 采样期间禁止转发，混出与移动归属分离 |
| 混合中再次 Play 跳权重 | 保留全部当前混合输入及权重，以其为下一次混合起点；从尚未完成的混出重新 Play 也保留当前层权重 |
| Playable 控制器参数与首次状态 | 接管前捕获当前状态、进度、参数和层权重；真实 Unity 控制器测试验证 SetFloat、SetTrigger 和根运动归属切换后的基础状态 |
| 已释放／宿主失效的驱动被缓存 | Animator、Legacy 与 Playable 驱动均提供有效性检查；SkillAnimator 检查驱动有效性，释放旧缓存并按当前 Animator 重建；释放路径用 finally 保证 Dispose |
| 多目标事件不能中途取消／查询截断 | 每个目标前检查施放上下文；物理缓冲饱和在发送任何事件之前报错 |
| 生成初始化中取消被当作失败 | 生成服务和初始化回调后检查施放身份；正常取消归还尚未托管对象并结束当前动作，不进入 ExecutionFailed，不继续数量循环 |
| 异步回调的归属依赖命中时机 | 独立任务回调及取消分支不能构建 Wait 或 Ability 归属任务；条件和模块继承该限制，构建错误携带动作 ID |
| 区域最近 N 个目标来回进出 | 区域使用完整候选结果判断离开，maxTargets 仅限制新占用者入场；已有占用者在真正离开前保留，避免重置进入效果 |
| 旧 AreaEffectSpawner 行为改变 | 恢复技能归属与旧 AreaEffectTask 查询缓冲语义；带时长的旧区域也会随能力取消。旧 ProjectileSpawner 同样保持技能归属；统一入口继续适配旧 RentVisual／ReturnVisual 对象池回调 |
| 未提供目标而飞向原点 | 区分未设置目标点与明确指定 `(0,0,0)`；已有目标 Transform 时在施放开始捕获其位置，两者都没有时在扣费前拒绝 |
| 一个场景任务异常使后续任务少推进一帧 | 使用复用的帧调度列表和任务运行版本；取消／移除不重复推进已处理任务，新启动或重启的任务留到下一帧。所有异常保留，单个异常原样抛出，多个异常聚合报告 |
| 周期调用模块重复分配 | 只冻结参数绑定；同步调用借用可复用作用域并更新目标点／事件。异步任务单独捕获空间数据；支持嵌套重入，稳定周期不重新分配作用域 |
| 回收来源导致项目过滤器收到 null | 未实现快照的过滤器在来源失效后停止接纳目标，不传 null；需要持续选敌的项目实现 ITargetFilterSnapshot |
| SetByCaller 字典被外部修改 | 先过应用门禁，仅持续效果或带子效果时复制并保留原比较器；周期与子效果共用快照，普通瞬时命中和被拦截的应用不复制 |
| 非有限持续时间／周期 | 拒绝 NaN／Infinity，检查发生在发布或刷新效果之前；技能冷却预检也在扣费前检查 |
| 重复 Grant 忽略新配置 | 显式 Grant 更新下一次施放的等级和授予参数；TryCast 只在尚未授予时隐式授予，不重置已有等级 |
| 套组撤销误收回其他来源技能 | 按授予来源保存贡献；显式授予替换临时隐式来源；Revoke 仅撤直接／临时授予，RevokeAll 明确撤销所有来源；撤销后恢复剩余来源的最新等级／参数 |
| 高等级激活额外分配 | 包含 Level 1 在内都冻结本次激活的等级，复用 Spec 的不可变默认等级上下文，预检查和提交共享同一份；升级后的首次构建仍允许分配 |
| 缺失消耗参数上下文直接抛异常 | Core／Unity 共用完整效果树预检，包含所有消耗操作、冷却 modifier 和延迟子效果；Core 激活返回 InvalidConfiguration，不打断其他技能、不扣费；低层效果直接 Apply 的配置错误仍明确抛出 |
| 旧任务路径缺少上下文／身份 | 旧 ProjectileTask、AreaEffectTask 与直接 AnimationTask 从所属 Spec 继承上下文，使用来源 incarnation 标识；无显式上下文时捕获来源值与等级 |
| Int／Bool 静默转换 | Int 拒绝非整数与溢出，Bool 数值只接受 0／1；类型化读取不再把其他类型静默转换 |
| 预览音效重叠与慢帧重播 | 每轨独立 AudioSource，窗口独占预览场景；退出区间仅释放对应通道，明确的 SeekVersion 决定跳转，不用帧间隔阈值 |
| 瞬时效果写入一半 | 先解析全部 modifier，再写属性；小批量用栈内缓冲，大批量租用数组，重入不会覆盖外层已解析数值 |
| onCancel 运行时错误波及独立任务 | 清理分支独立错误与资源范围；记录 LastCleanupError／LastCleanupActionId，不从 EndAbility 抛出，不取消先前场景任务，失败清理分支自己的资源仍回收 |
| 模块／事件覆盖目标点 | WithParameters／WithEvent 保留显式 TargetPosition；只有 WithTarget 重绑目标。模块每次调用刷新空间数据，等待任务保留创建时快照 |
| Source 接口变化 | 文档说明 AbilitySourceIdentity.Owner 及 Clear 后为 null，提供兼容裸 ASC 与身份对象的读取示例 |

## 第一轮回归（历史记录）

环境：Unity 6000.3.14f1，Windows 10，验证工程 `C:/Users/pc/Testproject/GASRefactorValidation~`。原工程通过 Packages 链接读取同一份包源码；未操作用户当前有未保存修改的场景。

- Core：22 组通过。保留 100,000 次激活／结束零分配断言；新增预热后 10,000 次 Level 3 激活／结束零分配断言。
- EditMode：146 项通过，规模测试为显式测试，本轮跳过。新增 27 个用例／参数组合覆盖此次边界、取消与资源归属问题。
- PlayMode：10 项通过，包含重新生成 BasicCombat 的五技能流程、独立投射物命中、引导取消、召唤及场景卸载清理。
- 额外编译：Runtime、Editor、Samples 与测试代码 0 错误、0 警告。
- Unity 分配检查：探针确认 ProfilerRecorder 能捕获真实分配后，预热的固定占用者区域执行 1,000 次 Tick，每次跨越两个周期并调用模块／发送事件，记录为 0 次托管分配。该结论不覆盖施放启动、首次绑定、占用者进入或对象生成。

主要新增用例位于 `Tests~/Core/SkillExecutionTests.cs`、`Tests~/Editor/SkillActionTests.cs`、`Tests~/Editor/SkillSectionTests.cs`。本机原始结果为 `.build~/hardening-editor-complete.xml`、`.build~/hardening-playmode-complete.xml`，日志同名。

Playable 参数互通是本机真实 Unity 控制器验证的结果；不是对任意 Unity 版本或项目自定义 StateMachineBehaviour 的兼容承诺。项目复杂过渡与完整角色根运动素材仍应做接入验收。没有把 Editor 权重断言描述成实际游戏画面的逐帧视觉验收。


## 第二轮审查回归

修正了第一轮测试没有覆盖的终点归属、完整效果预检、取消清理隔离、调度列表变动、字典比较器及模块目标点问题。

- Core 新增 `AuditRegressionTests`：全部消耗操作／冷却子效果预检、不提前打断、瞬时效果原子准备与重入、字典比较器与冻结、瞬时命中和被拦截效果零分配、任务调度删减／重启／多异常。
- EditMode 新增 Section 自循环不触发下一段、段结束不触发终点、取消回调失败不波及之前的场景任务、重复模块调用与异步上下文、授予来源、销毁后驱动重建、根运动设置重新捕获、Level 1 中途升级、音效重叠／慢帧／独立窗口清理。
- PlayMode 根运动测试先执行真实图求值，确认移动适配器确实收到回调；随后 Seek 必须收到实际 OnAnimatorMove，但适配器调用数不增加，避免 EditMode 空断言。
- 预览音频除通道状态测试外，增加两条重叠通道的实际 PCM 输出验证。

最终运行结果（Unity 6000.3.14f1，验证工程）：

- Core：23 组通过；100,000 次激活／结束 31.40ms、0 分配。另覆盖 10,000 次瞬时 SetByCaller 命中及被门禁拦截的持续效果，均不分配快照。
- EditMode：158 项通过，0 失败；1 项显式规模测试跳过。本轮未重跑性能规模基线。
- PlayMode：11 项全部通过，包含真实 OnAnimatorMove 对照／Seek 抑制和 BasicCombat 五技能流程。
- 合并编译：0 错误、0 警告。
- Unity ProfilerRecorder：预热的周期区域／模块 Tick 和重复同步模块调用保持 0 次分配；不包含施放启动或新任务创建。
- 音效：导入真实 WAV 后，两个重叠的独立 AudioSource 都输出非零 PCM；采样接口先建立 DSP 采集缓冲再读值。另检查 0.6 秒慢帧不 Seek、显式跳转、区间退出和窗口间资源隔离。

原始结果：`.build~/audit2-core-final.log`、`.build~/audit2-editor-final.xml`、`.build~/audit2-playmode-final.xml`，Unity 日志同名 `.log`。这些是本机自动回归，不等同于复杂项目动画过渡的手工视觉验收。

预览音频使用独立场景中的原生 AudioSource，不调用全局 StopAllPreviewClips；Unity 6.3 的预览场景音频标志与编辑器音频更新通过编辑器 API 接入。删除动画或预览报错时也停止本窗口音频，避免循环声音残留。

## 第三轮审查：实际时间、重入与状态交接（2026-11-03）

| 核对项目 | 本轮处理 |
|---|---|
| 跳转恢复的浮点重合 | 标签窗口、Cue 和已结束轨道均用与边界相同的实际时间比较；回归用两个不同授权值缩放后完全相同的浮点值，验证一次 OnActive、一次 Removed 和零残余标签 |
| 恢复阶段取消／再次跳段 | 从关闭旧状态前递增跳转版本；每个标签、Cue、轨道和驱动回调后检查版本与存活状态，放弃外层旧目的段的 Seek／采样 |
| ASC 任务列表在回调中变动 | 复用帧列表，记录任务运行版本；新增／重启留待下一帧；嵌套 Tick 不重复推进；推进异常在其他任务完成本帧后统一报告 |
| Clear 后丢失授予来源 | Runner 保留来源、等级和覆盖；下次 Grant／TryCast 重建 Spec。查询和撤销不创建 Spec，最后一个来源撤销后删除记录；首次构建失败也不留下无来源条目 |
| 模块错误无法定位 | 每经过 CallModule 边界，将错误定位到所属资产中的调用动作，内层异常保留具体模块和动作；校验可定位技能里的调用字段 |
| 暂停后仍有声音 | PlaybackModel.Playing 发出变化通知，时间轴暂停立即释放本窗口试听通道，恢复从当前播放头起播 |
| 慢帧声音偏移 | 窗口按实际编辑器时间推进，移除 0.1 秒上限；回归直接调用窗口时钟路径，覆盖 0.6 秒慢帧、暂停间隔与恢复 |
| 堆叠重施丢弃字典副本 | 持续效果通过 FindStackable 后，仅在新建效果之前复制 SetByCaller；新增预热堆叠零分配断言，原比较器与冻结语义保持 |
| Core 来源属性未冻结 | 激活提交前捕获来源值，晚启动的直接动画任务复用本次上下文；属性版本未变时复用不可变快照，保持原高等级激活零分配用例 |
| 复用目标点及 WithTarget(null) | 提供 ClearTargetPosition；WithTarget 没有 Transform 时保留显式落点，传入新 Transform 才重绑位置 |
| OnEnd 异常逃逸打断流程 | EndAbility 逐项清理标签和任务；错误记录在原 Spec.LastCleanupError，继续取消其他匹配技能，不取消早先独立存活对象。原“清理异常应抛出”测试更新为“清理完成并保留诊断” |
| Playable 交接 | 捕获未消费 Trigger；Dispose 先解绑输出，恢复当前状态、时间、层权重与参数。PlayMode 使用 Animator 的实时进度，编辑器手动 Evaluate 使用 playable 状态，避免真实帧中的进度回零 |
| Section 最后终点事件 | 保留 [start,end) 规则，新增 GAS_SECTION_END 警告并定位标记／任务事件；不连续进入下一段时，终点事件不执行 |
| Core／Unity 来源不一致 | GameplayEffectSpec 的 ASC 来源统一转为当次 AbilitySourceIdentity，自定义来源对象不变；动画 Cue 保留启动时的身份，Clear 收尾也不会归属下一次复用 |

原始结果保存在 `.build~/reentry-audit-core.log`、`.build~/reentry-audit-editmode.xml`、`.build~/reentry-audit-playmode.xml`，Unity 日志同名 `.log`。验证使用独立工程，不修改用户当前打开的场景；结果为自动化验证，不是复杂角色过渡的手工视觉验收。

最终验证（Unity 6000.3.14f1）：

- Core：24 组通过。100,000 次预热激活／结束为 34.64ms、0 字节分配；新增 ASC 任务 Tick 和 SetByCaller 堆叠重施的预热零分配断言通过。
- EditMode：176 项通过、0 失败；1 项显式规模测试跳过。本轮相对参数折叠修复后的 162 项基线新增 14 项用例／参数组合。
- PlayMode：12 项全部通过，包括生成的 BasicCombat、根运动 Seek 对照，以及 Playable 释放后连续多帧保持状态和继续推进时间。
- Runtime／Editor／Samples／Tests 合并编译：0 警告、0 错误；git diff --check 通过。

## 第四轮审查：公开入口、错误定位与测试接线（2026-11-03）

| 核对项目 | 本轮处理 |
|---|---|
| CallModule 施放／运行失败仍指向内部动作 | 每个模块验证边界包装错误；执行上下文保留最外层技能调用 ID 与模块路径，等待任务、独立区域和取消分支均继承。父上下文不再被内层动作 ID 覆盖，内层 ID 保留在错误消息中 |
| CheckActivation 只捕获部分计算异常 | 完整效果预检及消耗判断捕获自定义计算异常，返回 InvalidConfiguration，保留异常类型与消息；参数误用仍抛出参数异常 |
| Section 终点警告遗漏 | 所有 Section 终点附近的标记／任务事件都提示，包含一个浮点步长；覆盖自循环、非相邻 Next 与默认相邻但可能被 QueueSection 改写的情况，保持半开区间规则 |
| 属性变化后每次 Core 激活都分配快照 | Core 默认只固定等级，来源值实时读取；需要冻结时显式 captureSource:true。Unity SkillRunner 仍捕获施放快照，捕获时直接遍历属性，移除临时 List；不承诺捕获快照零分配 |
| FindFrom(definition, asc) 失效 | 查询和施加使用相同的 ASC → 当次 SourceIdentity 归一规则；旧身份可显式查询，自定义来源保持引用比较 |
| 原始任务失败被当作完成 | ASC 记录所属激活的失败并结束它，Runner 生命周期观察者显示执行失败；其他技能继续完成本帧，重启后的激活不被旧任务错误误伤 |
| 嵌套 Tick 吞时间 | ASC 与场景任务宿主均明确拒绝同实例重入，抛出 InvalidOperationException，并在接入文档中约定单一推进源 |
| Clear／RemoveAbility 丢失清理错误 | ASC 保留最近 64 条执行与清理诊断，不随授予删除或单位回收清空；调试窗口显示并可定位、显式清除，非通用事件日志 |
| 停用 Animator 时 Dispose 写回 | 仅对 isActiveAndEnabled 的 Animator 捕获与交还控制器状态，停用时仍释放图、时钟与根运动转发组件 |
| 开启边界潜在重复计数 | 标签仅补充尚未添加的项并累加持有计数；已开启 Cue 不重复派发 OnActive。强制回放同一开启边界的防御性用例验证最终计数归零 |

测试接线补强：音效回归现在使用 SkillEditorWindow 内实际 PreviewPanel 的预制体、采样器、音效开关及私有通道，直接通过 TimelinePanel.TogglePlay 和窗口 AdvancePlayback 驱动；测试不再手工订阅 Playback.Changed。EditMode 根运动在 Stop 前增加适配器调用正向对照；PlayMode 在 Stop 前再次真实求值，Stop 后必须仍收到 OnAnimatorMove 但不再调用移动适配器。另补不抛错、仅删除任务的 Core 调度用例。

原始结果使用 `.build~/audit4-core.log`、`.build~/audit4-editmode.xml`、`.build~/audit4-playmode.xml`，Unity 日志同名 `.log`。本轮不重跑 100 节点／500 技能显式规模基线；不将这些自动化结果描述为人工逐帧视觉验收。

最终验证（Unity 6000.3.14f1，独立验证工程）：

- Core：25 组通过；预热 10,000 次含消耗、冷却、DoT、资源恢复和变化属性的激活为 0 字节分配。原 100,000 次激活／结束为 32.59ms、0 字节；两个结果均不包含显式快照或 Unity 施放构建。
- EditMode：191 项通过、0 失败；1 项显式规模测试跳过。较上一轮新增 15 项用例／参数组合，已有音效和根运动测试改为实际接线及正反对照。
- PlayMode：12 项全部通过，含 Stop 前后的真实 OnAnimatorMove 对照、控制器交接、动画后端与生成 BasicCombat 流程。
- Runtime／Editor／Samples／Tests 合并编译：0 警告、0 错误；git diff --check 通过。

## 第五轮审查：段起点归属、真实来源读取与状态机回调（2026-11-03）

| 项目 | 验证与处理 |
|---|---|
| 共享边界误报 | 精确等于任一 Section 起点的事件属于目标段，免报 GAS_SECTION_END，不因上一段的 Next 或队列分支而改变；只有靠近排他终点、又不在起点时才提示。在 Unity 中用同一标记分别以 1 和 0.75 的比例运行：原速先于终点触发，缩放后两个 float 重合，标记被排他边界跳过。 |
| 旧任务启动时重复捕获来源 | ProjectileTask、AreaEffectTask 和 AnimationTask 继承本次激活的上下文。六个真实命中用例分别验证默认／冻结策略：攻击力从施放的 5 到启动的 11 再到命中的 17，实际扣除应为 17 或 5，不能是 11。 |
| 预检、提交与失败定位 | AbilityActivationCheck.Error 保留异常本身，支持堆栈和 InnerException；只读查询不追加诊断。Core、SkillRunner 的同步施放入口返回结果码；取消回调后按需复查，提交或启动失败不再伪装成功。已提交的费用／冷却及已经发生的打断不回滚。 |
| 任务与诊断 | ASC 任务 Begin、Tick 失败结束对应激活；保留首个主要失败，另外记录不同异常。跨层传播时识别已经记录的同一原因。场景任务保留原施放版本。环形记录保留最近 64 条，额外提供被覆盖数量；普通读取不格式化堆栈，可在调试窗口按需复制完整异常。 |
| 关闭期间重入 | 关闭回调重新跳段时，外层不再清理新段的状态；已经捕获的回调异常保留原堆栈并报告。回归同时检查异常对象、目标段 ID 和标签计数。 |
| 预览静音 | 实际 PreviewPanel 在音效关闭时播放不会创建试听通道；播放中关音效会立即释放已有 AudioSource，时间轴继续推进。 |

**Playable 的复现及修复。** 在 Unity 6000.3.14f1 用带 StateMachineBehaviour 的真实控制器测试：旧实现从非默认 Hit 状态交接时没有进入 Idle，但一直处于 Idle 时释放驱动，Idle 的进入次数从 2 变为 3。现在项目状态机始终拥有自己的输出，技能图用 PreviousInputs 读取前序姿势；释放时不执行 Animator.Update(0)、Play 或参数写回。PlayMode 同时核对待消费 Trigger、Float、状态时间持续推进、Idle 回调计数与骨骼位置（基础 2、半混合 4、技能 6、停止后 2）。这是 Unity 6.3 的实测，不代替各项目对自定义 Rig 输出顺序的验证。

原始结果：`.build~/audit5-core.log`、`.build~/audit5-editmode.xml`、`.build~/audit5-playmode.xml`；修复前的默认状态对照见 `.build~/audit5-handoff-idle.xml`。

最终回归：Core **26 组**、EditMode **200 项**、PlayMode **13 项**通过，0 失败；1 项显式编辑器规模测试跳过。Core 的 10,000 次预热施放在每轮用不同攻击力结算消耗，再改变来源攻击力、创建 DoT 并核对周期伤害，测得 0 字节托管分配；不含显式快照或 Unity 施放启动。Runtime、Editor、Samples 与测试代码合并编译 0 警告、0 错误。
