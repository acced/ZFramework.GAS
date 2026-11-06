# ZFramework GAS

Unity 的 Gameplay Ability System 实现。属性、标签、效果、能力、Cue 五层职责划分沿袭虚幻 GAS，
用地道 C# 实现，不照搬 UObject 反射与宏。

## 可配置技能

技能资产支持类型化动作、共享模块、Float / Int / Bool 参数与等级表。时间轴事件配置动作列表，无动画技能配置激活动作。投射物、区域和召唤默认由场景宿主调度；引导与等待属于施放实例。运行时调试已有五类快照、编辑器联动，可查看门禁结果、参数和动作状态。

双击时间轴标记可进入动作流程；在分支上创建、重排动作，或将连续动作提取为共享模块。Play 时可在流程节点按 F9，然后在运行时调试窗口查看单位的事件历史、参数和实际走过的分支。

动画支持曲线混合、Section 跳入过渡和片段替换。Playables 后端支持独立变速、按技能遮罩和按单位降频；玩法标记仍由 ASC 逐步推进。多目标预演和各后端的支持范围见 [编辑器指南](Docs~/EditorGuide.md)，真实窗口截图、规模数据和平台验证结果见 [五阶段改进验证](Docs~/EditorImprovementVerification.md)。

接入、生命周期、参数覆盖规则与配置示例见 [可配置技能工作流](Docs~/ConfigurableSkills.md)，从现有项目制作或导入资产见[项目接入](Docs~/ConfigurableSkills.md#从现有项目接入)。测试及限制见 [实施验证记录](Docs~/SkillImplementationVerification.md)。BasicCombat 示例由其生成器更新。

## 程序集划分

| 程序集 | 内容 | 依赖 |
|---|---|---|
| `ZFramework.GAS` | 规则本体：Attribute / Effect / Ability / Cue 路由、标签配置清单 | **mscorlib + System + ZFramework.GameplayTags（纯 C# Core）** |
| `ZFramework.GAS.Unity` | 需要引擎能力的部分：目标查询、投射物、区域效果、授权资产 | ZFramework.GAS + ZFramework.GameplayTags Core + UnityEngine |
| `ZFramework.GAS.Editor` | 技能结构图、动画时间轴编辑器 | ZFramework.GAS + ZFramework.GAS.Unity + Tags Core + UnityEditor |

两个 Core asmdef 都设为 `noEngineReferences: true`。Core 只能引用 Core，Unity 层引用 Core 和引擎；禁止反向引用。项目生成的标签词表属于项目，GAS 不引用 Generated 程序集。

规则层可在 .NET Standard 2.1 环境单独编译，传递依赖也不引用引擎：

```powershell
dotnet build ZFramework.GAS.Core.csproj -c Release
dotnet run --project Tests~/Core/Core.csproj -c Release
```

构建产物写入 `.build~/`，Unity 不会将其导入为插件。

## 包与命名

UPM 包名为 `com.acced.zframework.gas`，依赖 `com.acced.zframework.gameplaytags` 1.0.0。
程序集与命名空间统一为 `ZFramework.GAS`、`ZFramework.GAS.Unity`、`ZFramework.GAS.Editor`。
在使用 GAS 的 Unity 工程中，将下面这项加入 `Packages/manifest.json` 的 `dependencies`，固定 Tags 的 Git 来源和版本：

```json
"com.acced.zframework.gameplaytags": "https://github.com/acced/ZFramework.GameplayTags.git#1.0.0"
```

然后通过 Package Manager 的 **Add package from disk** 选择本仓库的 `package.json`。
GAS 包内 `package.json` 保持版本号依赖声明；UPM 的 Git URL 由项目 `manifest.json` 指定。

独立 .NET 构建默认使用相邻仓库 `../ZFramework.GameplayTags/GameplayTags.Runtime.csproj`。
本次迁移保留脚本和 asmdef 的 `.meta` GUID；接入方的自定义 asmdef 引用和 C# `using` 须改用上述新名称。

## 三分钟接入

### 1. 声明属性

库本身不定义任何具体属性——`AttributeId` 只是包装 int 的结构体，词表由你决定：

```csharp
public static class Attr
{
    public static readonly AttributeId Health   = new(1);
    public static readonly AttributeId MaxHealth = new(2);
    public static readonly AttributeId Attack   = new(3);
    public static readonly AttributeId AttackInterval = new(4);
}
```

### 2. 给单位挂上 ASC

```csharp
var asc = new AbilitySystemComponent();
asc.Attributes.Add(Attr.MaxHealth, 100f);
asc.Attributes.Add(Attr.Health, 100f);
asc.Attributes.Add(Attr.Attack, 20f);
```

### 3. 自己驱动 tick

**库不提供任何 tick 驱动器**，也不创建任何隐藏的 GameObject。dt 由你给定，因此时间步进完全可控：

```csharp
void Update() => asc.Tick(Time.deltaTime);
```

## 各子系统

### 属性

`BaseValue` 是权威值，`CurrentValue` 是只读派生值，由 `(Base + ΣAdd) × ΠMultiply ÷ ΠDivide` 聚合而来；
存在 Override 修改器时直接取其值。

```csharp
asc.Attributes.Get(Attr.Health).BaseValue -= 30f;      // 瞬时伤害改 BaseValue
float atk = asc.Attributes.GetCurrent(Attr.Attack);     // 读聚合后的值

// 订阅变化（UI 血条等）
asc.Attributes.Get(Attr.Health).OnCurrentValueChanged += (a, oldV, newV) => { };
```

`CurrentValue` **没有 setter**——持续加成一律走效果的修改器，避免出现第二个权威。

### 标签

点分层级，`"Ability.Attack.Melee"` 拥有 `"Ability"` 与 `"Ability.Attack"`，
但不拥有 `"Ability.Att"`（必须落在 `.` 边界上）。

```csharp
using GameplayTags;

// 词表由项目注册；在加载时缓存句柄，也可直接使用生成的 Tags.* 字段。
GameplayTag rage = GameplayTagManager.RequestTag("Buff.Rage");
GameplayTag stun = GameplayTagManager.RequestTag("Debuff.Stun");
asc.OwnedTags.AddTag(rage);
bool stunned = asc.OwnedTags.HasTag(stun);     // 层级匹配
```

`OwnedTags` 直接使用 `GameplayTagCountContainer`。同一标签可由多个效果、能力各贡献一次；移除次数由调用方保证不超过自己持有的显式计数。`RemoveTag` 返回 `void`。显式标签与隐式祖先都会产生计数通知，推荐按标签订阅：

```csharp
asc.OwnedTags.RegisterTagEventCallback(rage, GameplayTagEventType.NewOrRemoved,
    (tag, count) => SetRageVisible(count != 0));
```

`GameplayTagSet` 保留为不可变配置清单。Core 构造、增删和匹配的标签须为当前注册表已解析的非 None 句柄，查询热路径不增加校验、断言或告警。Unity 资产在定义构建边界过滤空行和未注册名称，解析到新数组，保留序列化字段原样；空或未注册的动画 Cue 区间跳过并告警。字符串隐式转换会查注册表，示例中的字符串仅用于配置构造；热路径复用生成字段或缓存句柄。

项目在首次标签使用前注册完整词表，域内冻结一次。Unity 使用生成的注册表；纯 .NET 可调用 `GameplayTagManager.Initialize(registrations)`。测试也要一次性注册全部词表。关闭 Domain Reload 时新增标签仍须重新生成并编译。

能力的 `ActivationRequirements`、效果的 `ApplicationRequirements` / `OngoingRequirements` 在定义构造时预计算；激活、应用和 tick 使用 `Matches`，不创建临时容器、不查名称。

从旧 GAS 标签升级：将原有的字符串 `GameplayTag` / `GameplayTagContainer` 用法替换为 Tags Core 类型，添加 `using GameplayTags;`；`Count` 改用 `ExplicitTagCount`，旧 `OnTagAdded/OnTagRemoved` 改为标签计数事件。Unity 授权字段改为 `GameplayTag[]` / `GameplayTag`，使用现有选择器，Definition 构建时解析一次。已有字符串资产需在升级时转换为内联 `{m_Name: 名称}` 数据；本项目未发现此类 GAS 资产，因此没有加入运行期兼容字段或迁移检查。

事件先提交状态再通知；回调抛错时剩余队列通知会被丢弃。效果删除仍逐项释放标签，保持结构清理。`Clear` 通知包含祖先，重入变更追加进同一队列。

### 效果

三层分离，对应 UE 的 `UGameplayEffect` / `FGameplayEffectSpec` / `FActiveGameplayEffect`：

```csharp
// 定义：不可变配置，多目标共享同一实例
var poison = new GameplayEffectDefinition(
    EffectDurationPolicy.Duration, duration: 5f, period: 1f,
    modifiers: new[] { new EffectModifier(Attr.Health, ModifierOperation.Add,
        EffectMagnitude.Fixed(-5f)) },
    tags: new EffectTags(granted: new GameplayTagSet("Debuff.Poison")),
    cueTags: new GameplayTagSet("Cue.Damage.Poison"));

// 规格：一次施加的意图（定义 + 来源）。需要稍后精确移除时保存 Handle。
target.Effects.Apply(new GameplayEffectSpec(poison, caster.Attributes, caster), out ActiveGameplayEffectHandle poisonHandle);
// target.Effects.Remove(poisonHandle);
```

数值来源三选一，**不解析运行期字符串公式**：

```csharp
EffectMagnitude.Fixed(-20f)
EffectMagnitude.FromAttribute(Attr.Attack, -1.5f, MagnitudeCapture.Source)  // 常数 + 系数×属性
EffectMagnitude.FromCaller("Damage")                                         // 施加方按键提供
```

**周期效果的修改器只按周期作用到 BaseValue，不挂成持续修改器**——
否则 DoT 会既扣血又永久压低该属性。

### 能力

消耗与冷却就是两个普通效果，与 UE 的 `CostGameplayEffectClass` / `CooldownGameplayEffectClass` 同构：

```csharp
var cooldown = new GameplayEffectDefinition(EffectDurationPolicy.Duration,
    durationAttribute: Attr.AttackInterval,          // 冷却时长取施法者的攻击间隔
    tags: new EffectTags(granted: new GameplayTagSet("Cooldown.BasicAttack")));

var basicAttack = new GameplayAbilityDefinition(
    abilityTags: new GameplayTagSet("Ability.BasicAttack"),
    cooldown: cooldown);

var spec = asc.GrantAbility(basicAttack);
switch (asc.TryActivateAbility(spec))
{
    case AbilityActivationResult.Activated:    break;
    case AbilityActivationResult.OnCooldown:   break;   // 冷却是显式检查的一步
    case AbilityActivationResult.CannotAfford: break;
    case AbilityActivationResult.Blocked:      break;
}
```

**任一检查失败都不产生副作用**：消耗不会扣、冷却不会上、标签不会授予。

「是否在冷却」= 「持有者是否拥有冷却效果授予的标签」，`spec.CooldownRemaining` 直接读剩余时长。

### 延迟动作（AbilityTask）

跨帧动作派生 `GameplayAbilityTask`，由所属能力持有：

```csharp
public sealed class DelayTask : GameplayAbilityTask
{
    private float _left;
    public DelayTask(float seconds) => _left = seconds;
    protected override void OnTick(float dt) { if ((_left -= dt) <= 0f) Complete(); }
    protected override void OnEnd(bool cancelled) { /* 回收资源 */ }
}

asc.StartTask(spec, new DelayTask(1.5f));
```

**任务的存活期是能力存活期的子集**：能力以任何方式结束（自然结束、被其他能力按标签取消、`Clear`），
其在跑任务立即取消并收到 `OnEnd(cancelled: true)`。该规则适用于 ASC 的所属任务；Unity 动作默认交给场景宿主的投射物可在技能结束后继续飞行。

### Cue（表现）

库只负责**按标签路由**，播什么怎么播由你实现：

```csharp
var router = new GameplayCueRouter();
router.Register("Cue.Damage", p => PlayHitVfx(p.Target, p.CueTag));   // 层级匹配
var asc = new AbilitySystemComponent(cueRouter: router);
```

三个时机对应 UE 的 `EGameplayCueEvent`：`Executed`（Instant 应用 / 周期跳动）、
`OnActive`（持续效果生效）、`Removed`（持续效果移除）。

Router 由你创建（通常一场战斗一个），不是单例，因此不存在跨场景悬挂。

## Unity 层

```csharp
// 目标查询：实例持有缓冲区，查询零分配
var searcher = new TargetSearcher(maxCandidates: 64);
var results = new AbilitySystemComponent[16];
int n = searcher.Sector(origin, forward, radius: 8f, angleDegrees: 90f,
    layerMask: mask, results: results, exclude: self);

```

扇形的角度判定在 **XZ 平面**进行（忽略高度差），适配地面战斗——
否则正上方的飞行单位会被判在扇形外。

### 授权资产

`GameplayEffectAsset` / `GameplayAbilityAsset` 让策划在 Inspector 里配置，无需写代码：

```csharp
var definition = myEffectAsset.Definition;    // 缓存复用同一实例
```

> **`Definition` 必须复用同一实例**——堆叠身份按定义引用判定。
> 若每次返回新实例，同一资产施加的效果将永远叠不上去。

## 技能动画时间轴

「第 N 帧造成伤害、第 M 帧起挂一段特效」是技能授权最核心的内容。
多种动画形式（Animator / 旧版 `Animation` / 序列帧 / Spine…）经**同一个接口**接进**同一条时间轴**：

```csharp
public interface IAnimationDriver
{
    float Play(string clipName, float duration, bool loop);   // 返回实际时长；找不到片段返回 0
    void Advance(float normalizedTime);
    void Stop();
}
```

**时长是下达的，不是查询的**。`Play` 把动画拉伸到给定秒数再播：引擎自己推进播放（保留过渡与混合），
时间轴在 gameplay 时钟上推进，两者按同一个时长走。因此不需要任何进度回查接口，也就不会漂移——
这正是战斗层原本手写在攻击分支里的 `state.speed = state.length / attackTime`，
这里把它变成所有动画形式共用的契约。

### 混入与混出

技能动画资产有「混入」「混出」两个时长（默认 0.12s / 0.18s）。驱动实现 `IBlendingAnimationDriver` 时，`AnimationTask` 按这两个时长平滑过渡，否则退回硬切：

| 驱动 | 混入 | 混出 |
|---|---|---|
| Animator | `CrossFadeInFixedTime` 到技能状态 | 淡回施放前所在的状态（通常是待机）；状态机自己的转换已离开技能状态时不插手 |
| Legacy Animation | `CrossFade` | 淡回施放前权重最高的片段 |
| Spine | 设置 `TrackEntry.MixDuration` | 混回施放前这条轨道上的动画；没有时混到空动画 |
| 序列帧 | 不支持，直接切换 | — |

**混入随实际时长缩放**：它是动作的一部分，攻速拉快时与标记点同比变短；混入不能超过动画时长（GAS014）。
**混出按实际秒数**：它发生在技能动画之后，与攻速无关。混出为 0 时驱动不切换状态，交给 Animator 自己的转换。
连续施放时，返回点保留最早的那个，连招结束后回到待机而不是上一个技能。

库内自带三个适配器：`AnimatorAnimationDriver`、`LegacyAnimationDriver`、`SpriteSequenceDriver`。
前两个由引擎自行推进，`Advance` 是空实现；序列帧在 `Advance` 里按进度换图。

标记的动作列表按顺序执行，名称只供显示，引用使用稳定 ID。Section 使用 `[start,end)`；连续进入下一段时只处理一次共享边界。技能时钟仍随 ASC.Tick 推进，即使视觉采样已降频。

### 通过技能资产施放

```csharp
var runner = new SkillRunner(asc, () => character.GetComponent<SkillAnimator>().Driver,
    new SkillUnitServices { Origin = character.transform, TargetRelations = MatchesTeam });
runner.Grant(skill, level: 3); // 或 abilitySet.GrantTo(runner)
var result = runner.TryCast(skill, new SkillCastTarget(targetAsc, aimPoint));
```

构建定义和编辑器校验负责配置正确性。运行时只判断实际玩法门槛（标签、冷却、资源），不再遍历动作预检。配置或项目回调抛错时，在宿主入口记录原始异常并结束能力；不聚合、重试或退款。调试事件是普通 C# event，订阅者不能抛异常。

等级、参数在施放时固定。同一授予配置下，反复使用同一份施放覆盖数组会复用解析结果；要改值请传新数组。一次性覆盖数组用完后可回收。Core 默认实时读取来源属性；要冻结值可显式传入 `new EffectEvaluationContext(level, parameters, sourceAttributes)`。`GameplayEffectSpec` 在确实需要持续持有 SetByCaller 时才复制，并保留字典比较器。

**不得在动画采样回调中施放或修改驱动**，包括 `OnAnimatorMove`、动画事件和 StateMachineBehaviour。将输入交给游戏循环处理。Playables 支持 CrossFade、Freeze、遮罩与根运动适配。技能权重归零后，Controller 负责自己驱动的骨骼；未绑定的武器挂点等由装备脚本或遮罩处理。禁用 SkillAnimator 或更换后端立即释放驱动。

### Spine（可选包）

`Runtime.Spine` 和 `Editor.Spine` 通过官方 `com.esotericsoftware.spine.spine-unity` 包的 4.x 版本定义开启。未安装时不参与编译；已有 Spine 3.8 不会启用此适配器。角色选择 `SpineBackend` 后，编辑器自动枚举动画并支持姿态采样。

本次在独立 Unity 6000.3.14f1 工程中使用官方 spine-unity 4.2.120 / spine-csharp 4.2.37 实际编译并通过 PlayMode 测试。没有升级使用方项目中的 Spine 3.8。4.0/4.1 的包版本尚未实测。

## 编辑器：技能工作台

入口：`Window → ZFramework GAS → 技能编辑器`，或双击 `GameplayAbilityAsset`。旧的“技能结构图”菜单和 `SkillGraphWindow.Open().Show(asset)` 调用重定向到新窗口。

窗口采用与设计网页接近的深色三栏：左侧技能库和大纲，中间结构图／能力设置及多轨时间轴，右侧角色预览和 Details。分隔条记忆尺寸；窄窗口的预览和 Details 改为页签。

资产仍是唯一运行期数据源。`SkillDocument` 共享序列化对象、选择和播放状态；命令负责 Undo 分组。节点增量同步保留节点实例和视口。位置、注释、预览角色及游离引用写入主资产 importer userData，不增加运行期图解释器。

| 操作 | 行为 |
|---|---|
| 新建技能 | 近战、投射物、Buff、持续伤害（打断后继续）、引导（随施放取消）、召唤骨架 |
| Space／右键／端口拖到空白 | 创建内嵌资产或引用项目资产，并连接类型兼容的端口 |
| 双击节点 | 在 Project 中定位来源资产 |
| 删除连线 | 只清除该引用 |
| 删除内嵌节点 | 解除当前技能引用并删除子资产；其他文件仍引用时阻止删除 |
| 删除外部节点 | 解除当前技能引用，保留共享资产文件 |
| Ctrl+C/V/D | 复制粘贴／复制节点；内嵌子图深拷贝，外部资产保留引用 |
| C、F、A | 注释框、聚焦选中、显示全部；工具栏可自动整理 |
| Details | 编辑所选资产或时间轴元素，并支持内嵌／外置转换 |
| 能力设置 | 标签门禁、调用方参数与门禁模拟器 |
| Ctrl+S、Ctrl+N、Ctrl+F | 保存、新建、搜索；Alt+左右键切换访问历史 |

消耗必须 Instant，冷却必须授予标签，子效果禁止自引用、重复挂载、循环和超过八层的链路。不兼容端口在拖线时不可连接，并提供原因提示。动画事件端口按稳定 ID 绑定，数组重排不改变身份。Cue 节点只读，显示来源效果或时间区间。

属性词表用 `AttributeCatalogAsset` 配置；没有词表时保留整数输入。带 `[AttributeCatalogSource]` 的代码常量可从菜单生成词表。Magnitude、修改器、SetByCaller 和 SerializeReference 后端／动作策略提供专用编辑控件。

### 多轨时间轴与预览

支持动画、事件、Cue 区间、标签窗口和标记动作。片段下拉从预览角色解析，选择片段时同步长度、帧率，并等比调整已有时间点。Animator 使用配置的层，递归子状态机，支持 Override Controller；BlendTree 使用第一个子片段作近似预览并提示。

| 操作 | 行为 |
|---|---|
| 拖刻度／播放头，Space | 定位姿态，播放／暂停 |
| 滚轮，中键拖动 | 缩放、平移 |
| 拖事件或区间，拖区间两端 | 修改时间、整体移动、调整起止 |
| Ctrl／Shift 选中，空白处拖框 | 多选；拖动选中组整体移动 |
| Alt+拖动 | 关闭吸附；默认吸附到帧、相邻元素或播放头 |
| 双击轨道／右键 | 添加元素；从大纲拖效果到事件轨道也可添加 |
| 拖动混入、混出手柄 | 动画条起点的斜坡与末尾的淡出尾巴显示过渡长度；拖动两端手柄或在片段行输入「混入」「混出」调整，按帧吸附，抬手一步撤销 |
| M、R | 在播放头添加事件；两次 R 标记 Cue 区间起止 |
| 逗号／句号、Home／End | 前后逐帧、首尾定位 |
| Ctrl+C/V、Delete | 复制、粘贴到播放头、删除选择 |

拖动过程只更新视觉位置，抬手一次写回并形成一步 Undo。时间轴元素的稳定 ID 由编辑器自动维护，用于保持数组重排后的端口和选择对应关系。

预览使用隔离的 PreviewRenderUtility 场景，不驱动战斗规则。拖动旋转、滚轮缩放、双击聚焦。角色的 `CueVfxPlayer` 引用 `CueVfxLibrary`，运行时与预览共用 Cue 标签 → 特效 Prefab、挂点路径、是否跟随、停止方式的映射。普通 ParticleSystem 支持拖动采样与尾迹，无需每标签处理器；自定义网格实现 `CueVfxBehaviour`，其他预览扩展仍可实现 `ICuePreview`。顶部后端菜单切换预览角色上已有 SkillAnimator 的策略。

### 校验与示例

消息页提供 GAS001–004、010–013、020–022、030–032 检查、定位和适用的自动修复。需用户选择的配置仍在 Details 中处理，不会猜测标签或资产。菜单 `Tools → ZFramework GAS → 校验所有技能` 可批量检查；CI 可调用 `ZFramework.GAS.Editor.BatchValidate.Run`，存在错误时退出码非零。批量校验不会为资产补 ID。

`Tools → ZFramework GAS → 创建 BasicCombat 示例` 程序化生成骑士与训练假人、六段动作、技能特效、五个技能槽（六个技能资产）和可游玩的训练场，并把示例标签写入项目标签配置。重新生成会先确认，再覆盖示例目录；详见 `Samples/BasicCombat/README.md`。


## 需要知道的几条语义

1. **堆叠身份 = 定义的引用相等**。共享同一个 definition 实例的效果才可能相互堆叠。
2. **冷却不是计时器，是标签**。冷却效果授予一个标签，能力据此判定；时长即该效果的剩余时长。
   外部移除该效果（`RemoveEffectsWithAnyTag`）会让能力立刻可用。
3. **`SetByCaller` 键缺失会抛异常**，不会静默返回 0——配置错误应当立刻暴露。
   技能定义应提供有限的数值，宿主应传入非负的 Tick 时间。开发构建的使用断言不构成发布版的输入校验。
4. **能力所属任务随能力结束；场景任务独立存活。**
   任务在遍历目标的过程中若因连锁反应被取消（如击杀触发的打断），会当场停手——
   已死的任务一发都不会再打。
5. **`OngoingBlocked` 在 tick 内检查**，移除最多延迟一个 tick（60Hz 下 16ms）。
   代价换来的是没有任何事件订阅，因而不存在订阅泄漏。
6. **谁施加的效果由谁移除**。区域光环用 `FindFrom(definition, source)` 取得稳定 Handle，
   再以 `Remove(handle)` 移除；两个同类光环重叠时互不干扰。`Find(definition)` 只按定义找，
   用于「有没有这个冷却」这类查询。效果移除后旧 Handle 失效，绝不会因内部对象复用而指向新效果。
7. **空间查询看到的是上一次物理同步时的位置**。项目若关闭 `Physics.autoSyncTransforms`
   （Unity 默认就是关），当帧内用代码挪动的 Transform 不会反映到查询里。
   这是 Unity 所有物理查询的统一行为，库不代为同步。
8. **区域的占用者缓冲区被填满时，只加不减**。查询返回的单位一定在范围内，
   所以「加入」在结果被截断时仍然成立；「移除」不成立——据不完整结果移除会让光环每帧反复施加。
   把 `maxOccupants` 配够即可，不再截断后会自动恢复。
9. **带周期的效果，其修改器每次跳动结算一次，不会常驻**（与 UE 的 periodic GE 一致）。
   「常驻攻击力加成 + 每秒回血」要拆成两个效果，用子效果链
   （`ConditionalEffect(regen, EffectTrigger.Applied)`）绑在一起。
   子效果只在真正新建 ActiveEffect 时触发，**堆叠时不重复挂**。

## 完整范例

运行菜单 `Tools → ZFramework GAS → 创建 BasicCombat 示例`，在 `Assets/GAS Samples/BasicCombat` 生成一套不依赖任何第三方资源的完整示例，详见 `Samples/BasicCombat/README.md`。

| 技能 | 用到的机制 |
|---|---|
| 斩击 | 体力消耗、冷却、命中伤害（按攻击缩放）、剑光 Cue 区间、霸体与连招窗口 |
| 能量弹（分叉闪电 / 逐个弹射） | Q 切换第 2 槽资产；两份技能共用参数、消耗和冷却 |
| 战吼 | 作用于自身的持续增益、光环 Cue、霸体窗口 |
| 圣域引导 | 周期治疗、引导标签窗口阻断其他技能、法阵 Cue |
| 召唤 | 时间轴动作生成守护灵与持续区域，按配置寿命独立存活 |

训练场场景 `BasicCombat.unity` 中的 `TrainingArena` 在单位初始化时绑定场景服务和阵营规则；输入只选择技能资产和目标，ASC 由训练场按固定步长推进。斩击和能量弹筛敌方，圣域治疗友方，筛选条件在技能资产中。`CombatUnit` 初始化时调用 `CueVfxPlayer.Bind(asc)`，时间轴特效由共享配置驱动。

菜单 `Tools → ZFramework GAS → 创建 BasicCombat 示例` 同时生成独立特效、共享映射库并完成角色接线，全部资源保存在 `Assets/GAS Samples/BasicCombat` 内。战吼技能的 `Cue.Ground.Sigil` 在 0.2～0.9 秒显示普通粒子法阵，固定在地面并在结束后保留尾迹。映射位于该目录的 `CueVfxLibrary.asset`，新增特效的步骤见 [BasicCombat 说明](Samples/BasicCombat/README.md)。

## 宿主约定

- 在主循环中每帧推进每个 ASC 和场景宿主一次；不要在任务、效果或事件回调里重入 Tick。所属任务按帧快照遍历，回调取消的任务不会再次推进。
- 运行时观察事件是同步通知。不要依赖监听器隔离、取消时的错误历史或自动恢复。
- `GameplayEffectSpec(source: asc)` 使用 `AbilitySourceIdentity` 区分回收之前和之后的单位。`FindFrom(definition, asc)` 仍可按当前身份查找。
- 骨骼、Prefab、物理和项目移动逻辑由 Unity 层接入；Core 不扫描类型，也不执行图或字符串脚本。

见[编辑器指南](Docs~/EditorGuide.md)和[验证记录](Docs~/EditorImprovementVerification.md)。
