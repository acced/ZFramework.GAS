# BasicCombat

在 Unity 菜单选择 **Tools → ZFramework GAS → 创建 BasicCombat 示例**。生成器全部由代码程序化构建，不依赖任何第三方美术资源，结果写入 `Assets/GAS Samples/BasicCombat`：

| 目录 / 文件 | 内容 |
|---|---|
| `Prefabs/Knight` | 持剑盾的骑士：人形骨骼、头盔面罩辉光、羽饰、披风、纹章盾；挂好 `Animator` 与 `SkillAnimator` |
| `Prefabs/TrainingDummy` | 训练假人，带受击摇晃动画 |
| `Prefabs/ArcaneBolt` | 能量弹的飞行外观（发光核心、拖尾） |
| `CueVfxLibrary.asset` | Cue 标签 → 特效 Prefab、挂点、跟随和停止方式的共享配置 |
| `VFX/` | 独立的刀光、蓄力球、法阵与地面粒子特效 Prefab |
| `Animations/` | 待机与五个技能动作（IK 驱动四肢、披风与羽饰弹簧跟随），假人受击 |
| `Skills/` | 五个技能，效果与动画内嵌为子资产，时间轴已对齐动作关键帧 |
| `BasicCombat.unity` | 训练场：石台、立柱旗帜、火盆、骑士与假人 |
| `Attributes.asset` | 属性词表：生命 1、最大生命 2、攻击 3、体力 4、护甲 5 |
| `BasicCombatAbilities.asset` | 初始技能套组与授予等级 |
| `Skills/Shared/` | 扇形斩击模块、共享伤害与范围参数、重斩变体和斧劈片段替换表。重斩复用同一时间轴，通过变体覆盖两个伤害系数（×1.5）和动作片段；冷却仍属于技能。 |

生成器还会把示例标签（`SampleTags.All`）加入项目的 Gameplay Tags 配置并重新生成注册表，随后 Unity 会编译一次。重新生成前会弹窗确认，并覆盖整个示例目录。

## 在技能编辑器里看

双击斩击的命中标记进入流程，再双击模块查看条件的两支。训练场按 **E** 切换剑斩／斧劈动作，再按 **1** 施放；伤害、标记时刻和冷却不变。斩击演示相机震动和局部顿帧，能量弹演示瞄准，战吼演示短时全局减速。示例通过 `ArenaPresentation` 接入项目服务。

生成完成后会自动打开「斩击」。技能栏授予的六份技能资产都预设了骑士作为预览角色：拖动时间轴播放头即可在预览视口看到动作。预览读取骑士上 `CueVfxPlayer` 引用的 `CueVfxLibrary.asset`，进入 Cue 区间后实例化特效。普通粒子支持前后拖动采样和停止发射后的尾迹；刀光仍通过烘焙轨迹重建网格。

## 能量弹：Q 切换，2 施放

打开生成的 BasicCombat 场景。技能栏第 2 格显示当前模式，默认「分叉闪电」。按 **Q** 只切换下一次施放的模式，按 **2** 或点击第 2 格释放。两种模式共用体力与冷却。

- **分叉闪电**：首弹实际撞上 A 后，在 A 的命中点周围选最多两名不同的敌人，同时显示 A→B、A→C 的闪电并造成伤害。C 够不到 A 时，不会通过 B 补选。
- **逐个弹射**：A 命中后发出第二颗弹，命中 B 后再从 B 附近选 C。没有合适的下一目标就停止。已经离手的弹独立于动画，按 Q 不会改变在途弹。
- 分别打开「分叉闪电」或「逐个弹射」的动画 → 发射，查看各自的有限目标序列。参数页可改总目标数（含首击，默认 3）、搜索半径（3.5 米）、速度和每跳保留比例（80%）。分叉的两个次目标都在第 1 跳，伤害均为 80%；逐个弹射则依次为 100%、80%、64%。序号从 0 开始，倍率作用在扣除防御后的伤害上。
- 打开 **Tools → ZFramework GAS → BasicCombat → 连锁路线预演**，点窗口中的技能名切换变体。分叉从首个命中点同时放电，弹射从上一跳命中点发出飞弹；点击路线时间可定位到命中。Q 只在训练场切换第 2 槽资产。
- 要看出搜索原点的区别，在示例窗口的「预演设置」选「排布 → 接力」、假人数量 3、瞄准假人 1，目标距离保持 2。按示例默认半径，#1 能搜到 #2，#2 能搜到 #3，#1 搜不到 #3。分叉只命中两人；逐个弹射能命中三人。

![分叉和弹射的机制对照（旧版界面示意）](../../Docs~/Images/EditorImprovements/energy-bolt-routes.png)

图中是资产拆分前的路线对照；当前入口是分别打开两个技能资产。

## 添加独立特效

1. 在 Gameplay Tags 配置中注册 Cue 标签。
2. 在 `CueVfxLibrary.asset` 新增映射：选择标签、拖入特效 Prefab，填写相对骑士根节点的挂点路径（空为根节点）。
3. 需要跟随手、剑等节点时勾选“跟随挂点”；地面特效取消勾选，并配置位置／旋转偏移。
4. 选择“立即移除”或“停止发射，保留尾迹”，后者的最长时间用于兜底回收循环粒子。
5. 在技能动画时间轴添加同名 Cue 区间。不需要新增运行时回调或每标签预览类。

菜单 **Tools → ZFramework GAS → 创建 BasicCombat 示例** 会一次生成全部角色、技能、独立特效 Prefab、共享映射库和训练场，所有生成资源都位于 `Assets/GAS Samples/BasicCombat`。骑士自动引用此目录的 `CueVfxLibrary.asset`，五个技能自动绑定该骑士作为预览角色，无需生成后手动接线。打开 `BasicCombat.unity`，按 **3** 施放战吼：0.2～0.9 秒开启 `Cue.Ground.Sigil`，角色侧面出现绿色地面法阵及上浮粒子。它不跟随角色，区间结束后停止发射并保留粒子尾迹，最多 1.5 秒。打开 `Skills/战吼.asset`，可用编辑器时间轴预览同一特效。

## 在训练场里玩

打开 `BasicCombat.unity` 点 Play，按 1–5（或点击技能栏）施放技能：

| 键 | 技能 | 演示的机制 |
|---|---|---|
| 1 | 斩击 | 体力消耗、冷却、命中伤害、剑光、霸体与连招窗口 |
| 2 | 能量弹 | Q 切换分叉闪电／逐个弹射；从命中点选敌，动画结束后仍能命中 |
| 3 | 战吼 | 自身增益（攻击 +10，持续 6 秒）、强化标签、光环；随后斩击进入强化分支 |
| 4 | 圣域引导 | 周期治疗范围内的友方（含自身）；打断会结束治疗，引导期间的 `State.Channeling` 阻断其他技能 |
| 5 | 召唤 | 生成守护灵动作＋独立持续领域；存活时间、数量、周期、范围均由参数配置 |

`CombatUnit` 持有 ASC 和每个角色独立的 `SkillRunner`。训练场在初始化时绑定出手点、场景服务和敌友关系，通过技能套组授予资产。所有技能都走 `hero.SkillRunner.TryCast(asset, new SkillCastTarget(dummy.AbilitySystem, aimPoint))`。要看攻击和治疗选了谁，在技能编辑器中打开相应动作，查看目标查询的「目标关系」。召唤由资产里的 `SpawnPrefabAction` 执行。

`Runtime/TrainingArena.cs` 负责创建 `GameplayCueRouter` 和在 `FixedUpdate` 中推进各单位。`CombatUnit.Initialize` 将 ASC 绑定给 `CueVfxPlayer`，通用播放器负责时间轴特效的创建、LateUpdate 更新和清理；训练场显示命中反馈和 HUD。HUD 用 IMGUI 绘制，按键也经 IMGUI 事件读取，新旧输入系统下都能运行。

## 代码结构

- `Runtime/`：`CombatUnit`（单位与属性）、`TrainingArena`（宿主）、`CueVisual` / `PulseVisual` / `SwordTrailVisual`（特效），`SampleTags`。
- `Editor/`：`BasicCombatSample`（入口）、`CharacterBuilder` / `MeshKit` / `SampleArt`（程序化美术）、`PoseRig` / `KnightAnimations`（姿势与动作烘焙）、`SampleVfx`（独立特效 Prefab 与映射库生成）、`SampleSkills`、`SceneBuilder`。
