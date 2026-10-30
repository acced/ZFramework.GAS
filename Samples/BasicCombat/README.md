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
| `Attributes.asset` | 属性词表：生命 1、最大生命 2、攻击 3、体力 4 |

生成器还会把示例标签（`SampleTags.All`）加入项目的 Gameplay Tags 配置并重新生成注册表，随后 Unity 会编译一次。重新生成前会弹窗确认，并覆盖整个示例目录。

## 在技能编辑器里看

生成完成后会自动打开「斩击」。五个技能都预设了骑士作为预览角色：拖动时间轴播放头即可在预览视口看到动作。预览读取骑士上 `CueVfxPlayer` 引用的 `CueVfxLibrary.asset`，进入 Cue 区间后实例化特效。普通粒子支持前后拖动采样和停止发射后的尾迹；刀光仍通过烘焙轨迹重建网格。

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
| 2 | 能量弹 | 蓄能 Cue、`ProjectileSpawner` 任务事件与命中效果 |
| 3 | 战吼 | 自身增益（攻击 +10，持续 6 秒）、光环 |
| 4 | 圣域引导 | 周期治疗；引导期间的 `State.Channeling` 阻断其他技能 |
| 5 | 召唤 | 纯回调事件，宿主在法阵处生成守护灵 |

`CombatUnit` 持有 ASC 和每个角色独立的 `SkillRunner`。`TrainingArena` 通过 `hero.SkillRunner.TryCast(skill.asset, dummy.AbilitySystem, context)` 请求施放，并按资产查询冷却和激活状态，不再持有 Spec 或创建 `AnimationTask`。统一入口负责授予去重、激活、创建动画任务和播完结束技能；`MarkerReached` 把技能资产与时间轴事件交给训练场处理召唤。

`Runtime/TrainingArena.cs` 负责创建 `GameplayCueRouter`、`FixedUpdate` 推进，以及为投射物提供出手点、目标查询与对象池回调。`CombatUnit.Initialize` 将 ASC 绑定给 `CueVfxPlayer`，通用播放器负责时间轴特效的创建、LateUpdate 更新和清理；训练场仅保留命中反馈等游戏逻辑。HUD 用 IMGUI 绘制，按键也经 IMGUI 事件读取，新旧输入系统下都能运行。

## 代码结构

- `Runtime/`：`CombatUnit`（单位与属性）、`TrainingArena`（宿主）、`CueVisual` / `PulseVisual` / `SwordTrailVisual`（特效），`SampleTags`。
- `Editor/`：`BasicCombatSample`（入口）、`CharacterBuilder` / `MeshKit` / `SampleArt`（程序化美术）、`PoseRig` / `KnightAnimations`（姿势与动作烘焙）、`SampleVfx`（独立特效 Prefab 与映射库生成）、`SampleSkills`、`SceneBuilder`。
