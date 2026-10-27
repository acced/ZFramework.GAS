# ZFramework GAS

Unity 的 Gameplay Ability System 实现。属性、标签、效果、能力、Cue 五层职责划分沿袭虚幻 GAS，
用地道 C# 实现，不照搬 UObject 反射与宏。

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
其在跑任务立即取消并收到 `OnEnd(cancelled: true)`。因此投射物这类动作应让能力保持激活到落点。

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

// 投射物：视觉体由你创建与回收，任务只负责移动与命中
asc.StartTask(spec, new ProjectileTask(config, searcher, caster.Attributes, caster,
    onFinished: t => ReturnToPool(t.Visual)));

// 区域效果：进入施加、离开按配置移除
asc.StartTask(spec, new AreaEffectTask(areaConfig, searcher, caster.Attributes, caster));
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

库内自带三个适配器：`AnimatorAnimationDriver`、`LegacyAnimationDriver`、`SpriteSequenceDriver`。
前两个由引擎自行推进，`Advance` 是空实现；序列帧在 `Advance` 里按进度换图。

时间轴本身是一段不可变定义，交给 `AnimationTask` 推进：

```csharp
var swing = new SkillAnimationDefinition("Attack", 0.8f,
    markers: new[]
    {
        new AnimationMarker(0.35f, "命中", damageEffect),                   // 到点施加效果
        new AnimationMarker(0.55f, "收招"),                                 // 纯回调，作用对象现场决定
    },
    cueRegions: new[] { new AnimationCueRegion(0.1f, 0.5f, trailTag) });    // 起止各派发一次 Cue

asc.StartTask(spec, new AnimationTask(swing, driver, target, castTime,
    onMarker: (task, marker) => { if (marker.Key == "收招") SpawnShockwave(); },
    onFinished: _ => asc.EndAbility(spec)));
```

- **时间单位一律是秒**。资产上的 `frameRate` 只供时间轴 UI 把秒显示成帧，不参与任何运行期计算
- 标记点的时间相对**授权时长**；实际时长与它的比值就是缩放系数，
  因此攻速变化时命中仍落在挥砍的同一个相对位置
- 区间映射到一对已有的 Cue 事件（`OnActive` / `Removed`），不引入新概念；`end < 0` 表示随动画结束
- **循环动画每圈重新触发标记点与区间，且不自行结束**，由能力结束它；非循环跑满时长即 `Complete()`
- 单帧跨过多个标记点时全部按序触发；跨过整圈时本轮只结算到圈末，余下时间留给下一圈
- 挂了「作用于固定目标」的效果标记点却没传目标 → **构造时抛异常**；标记点越出时长同样抛异常；
  两个标记点用同一个 `Key` 也抛——回调靠 `Key` 分辨是哪一下，重名就分不清了
- 驱动找不到片段（返回 0）时**时间轴照常推进**：表现缺失不该连带把判定一起丢掉。
  此时 `Stop()` 仍会照常调用一次，因此驱动必须自己分辨播没播过，不能无条件复位共享状态
- `onFinished` 只报告，不代为结束能力——收招后还有后摇的技能不该被替它结束掉

### 通过 SkillAnimator 接入动画后端

角色挂载 `SkillAnimator`，在 Backend 下拉选择 Animator、Legacy Animation 或序列帧。驱动按需创建并缓存；已有手动创建 `IAnimationDriver` 的宿主仍可继续使用。

```csharp
var driver = character.GetComponent<SkillAnimator>().Driver;
var task = new AnimationTask(abilityAsset.Animation.Definition, driver,
    target: targetAsc);
owner.StartTask(spec, task);
```

`AnimationTask` 完成时只报告完成；宿主按技能生命周期决定是否结束能力。任务事件需要向构造函数的 `taskContext` 传入 `TimelineTaskContext`，提供 Origin、Searcher、目标和视觉对象池回调。内置 ProjectileSpawner / AreaEffectSpawner 会创建任务对象，这部分不承诺零分配。

`tagWindows` 在区间内计数授予标签，离开、打断和取消时回滚自己持有的计数；`taskEvents` 在指定时间调用可扩展的 `TaskSpawner`。同一帧跨过多个时间点时按边界顺序处理，命中回调能观察到该时刻的标签状态。

### Spine（可选包）

`Runtime.Spine` 和 `Editor.Spine` 通过官方 `com.esotericsoftware.spine.spine-unity` 包的 4.x 版本定义开启。未安装时不参与编译；已有 Spine 3.8 不会启用此适配器。角色选择 `SpineBackend` 后，编辑器自动枚举动画并支持姿态采样。

本次在独立 Unity 6000.3.14f1 工程中使用官方 spine-unity 4.2.120 / spine-csharp 4.2.37 实际编译并通过 PlayMode 测试。没有升级使用方项目中的 Spine 3.8。4.0/4.1 的包版本尚未实测。

## 编辑器：技能工作台

入口：`Window → ZFramework GAS → 技能编辑器`，或双击 `GameplayAbilityAsset`。旧的“技能结构图”菜单和 `SkillGraphWindow.Open().Show(asset)` 调用重定向到新窗口。

窗口采用与设计网页接近的深色三栏：左侧技能库和大纲，中间结构图／能力设置及多轨时间轴，右侧角色预览和 Details。分隔条记忆尺寸；窄窗口的预览和 Details 改为页签。

资产仍是唯一运行期数据源。`SkillDocument` 共享序列化对象、选择和播放状态；命令负责 Undo 分组。节点增量同步保留节点实例和视口。位置、注释、预览角色及游离引用写入主资产 importer userData，不增加运行期图解释器。

| 操作 | 行为 |
|---|---|
| 新建技能 | 近战、投射物、Buff、引导、召唤骨架五种模板 |
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

属性词表用 `AttributeCatalogAsset` 配置；没有词表时保留整数输入。带 `[AttributeCatalogSource]` 的代码常量可从菜单生成词表。Magnitude、修改器、SetByCaller 和 SerializeReference 后端／任务策略提供专用编辑控件。

### 多轨时间轴与预览

支持动画、事件、Cue 区间、标签窗口和任务事件轨道。片段下拉从预览角色解析，选择片段时同步长度、帧率，并等比调整已有时间点。Animator 使用配置的层，递归子状态机，支持 Override Controller；BlendTree 使用第一个子片段作近似预览并提示。

| 操作 | 行为 |
|---|---|
| 拖刻度／播放头，Space | 定位姿态，播放／暂停 |
| 滚轮，中键拖动 | 缩放、平移 |
| 拖事件或区间，拖区间两端 | 修改时间、整体移动、调整起止 |
| Ctrl／Shift 选中，空白处拖框 | 多选；拖动选中组整体移动 |
| Alt+拖动 | 关闭吸附；默认吸附到帧、相邻元素或播放头 |
| 双击轨道／右键 | 添加元素；从大纲拖效果到事件轨道也可添加 |
| M、R | 在播放头添加事件；两次 R 标记 Cue 区间起止 |
| 逗号／句号、Home／End | 前后逐帧、首尾定位 |
| Ctrl+C/V、Delete | 复制、粘贴到播放头、删除选择 |

拖动过程只更新视觉位置，抬手一次写回并形成一步 Undo。稳定 ID 在打开旧资产时补齐，也可使用 `Tools → ZFramework GAS → 迁移 → 为时间轴元素补 ID`；原有字段名和数组顺序不变。

预览使用隔离的 PreviewRenderUtility 场景，不驱动战斗规则。拖动旋转、滚轮缩放、双击聚焦；Cue 默认显示文字，项目可实现 `ICuePreview` 扩展表现。顶部后端菜单切换预览角色上已有 SkillAnimator 的策略。

### 校验与示例

消息页提供 GAS001–004、010–013、020–022、030–032 检查、定位和适用的自动修复。需用户选择的配置仍在 Details 中处理，不会猜测标签或资产。菜单 `Tools → ZFramework GAS → 校验所有技能` 可批量检查；CI 可调用 `ZFramework.GAS.Editor.BatchValidate.Run`，存在错误时退出码非零。批量校验不会为资产补 ID。

`Tools → ZFramework GAS → 创建 BasicCombat 示例` 生成五种技能、属性词表、剑士预制体和 0.8 秒 Animator 动作。普攻包含刀光 Cue、霸体和连招窗口。生成器不会覆盖已有技能；标签注册与宿主接入见 `Samples/BasicCombat/README.md`。


## 需要知道的几条语义

1. **堆叠身份 = 定义的引用相等**。共享同一个 definition 实例的效果才可能相互堆叠。
2. **冷却不是计时器，是标签**。冷却效果授予一个标签，能力据此判定；时长即该效果的剩余时长。
   外部移除该效果（`RemoveEffectsWithAnyTag`）会让能力立刻可用。
3. **`SetByCaller` 键缺失会抛异常**，不会静默返回 0——配置错误应当立刻暴露。
   同理，属性算出 `NaN` / 无穷（含 `Divide` 除数为 0）、`Tick` 传入负 dt、
   给修改器传空来源，都会抛异常而非静默接受。
4. **任务生命周期从属于能力**。想让动作在能力结束后继续，是设计错误。
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

`Samples/BasicCombat/README.md` 说明本次重建的示例。运行菜单 `Tools → ZFramework GAS → 创建 BasicCombat 示例` 后，在项目 Assets 下生成五种技能和预览角色。

| 技能 | 内容 |
|---|---|
| 普攻_剑士 | 体力消耗、冷却、命中伤害、刀光 Cue、霸体及连招窗口 |
| 投射物 | 发射任务与命中效果；需补视觉预制体和宿主查询／对象池服务 |
| 狂暴 Buff | 持续攻击加成和冷却 |
| 引导 | 周期效果和动画事件 |
| 召唤骨架 | 宿主回调事件；单位生成仍由项目实现 |

这是可编辑的资产与动画样例，不包含完整战斗场景。此前 README 提及的 CombatArena / CombatUnit 演示源码不在当前仓库中；旧验证记录保留为历史，不视为本次样例的端到端验收。


## 重入保证

回调里回过头操作系统本身是常见需求（命中即结束技能、进入区域即打断引导）。以下行为经断言固定：

| 在回调中做的事 | 结果 |
|---|---|
| 任务的 `OnTick` 里结束 / 移除自己所属的能力 | 能力正确结束，任务收到一次 `OnEnd(cancelled: true)`，激活期标签清理 |
| 任务的 `OnTick` 里启动新任务 | 新任务**下一帧**才开始推进，与「在 tick 之外启动」一致 |
| 任务的 `OnTick` 里 `Clear()` 整个 ASC | 能力、任务、效果、标签全部清空，无残留 |
| 任务的 `OnEnd` 里再次 `Complete()` | 无效果，不会重复触发 `OnEnd` |
| 任务的 `OnEnd` 里启动新任务 | **抛异常**——能力已结束，不允许再挂任务 |
| 任务激活的能力反过来取消了本能力 | 本任务被取消，标签清理，无崩溃 |
| Cue 处理器在 tick 中 `Clear()` 效果容器 | 推迟到本帧 tick 结束后执行，不破坏遍历 |
| Cue 处理器在派发中注销自己或别人 | 槽位置空、派发结束后压缩；同一次派发里其余处理器一个不漏 |
| Cue 处理器施加的效果又派发了 Cue（嵌套派发） | 按深度计数，内层退出不影响外层；压缩只在最外层做 |
| Cue 处理器在派发中注册新处理器 | 本次不派发，下一次生效 |
| 标签移除回调里增删其他标签 / 再次 `Clear()` | 正常回调的重入通知排队派发；回调抛错则丢弃剩余通知；`Clear` 期间新增且未再次清除的标签保留 |
| Cue 处理器在 `OnActive` 中移除刚应用的效果 | 修改器与标签正确回滚；`Apply` 仍返回 `Applied`，其 `Applied` 子效果照常触发 |
| 属性变更回调里改同一属性（如钳制血量上限） | 收敛即可，两次迭代内稳定 |
| 投射物命中目标时，效果连锁取消了发射者的能力 | 当场停手，后续目标不再受击 |
| 区域施加光环时，效果连锁取消了所属能力 | 当场停手；已入区者由 `OnEnd` 回滚，后续单位不会被拉进已结束的区域 |
| 子效果链成环 | 深度超过 8 层抛异常；容器状态不受影响，后续调用正常 |
| 标签窗口开启回调取消能力 | 回滚本窗口已授予的计数，不移除其他来源的标签 |
| 单帧跨过完整标签窗口和命中事件 | 按时间边界依次开窗、命中、关窗；命中观察到正确标签状态 |
| 关闭窗口期间标签回调抛异常 | 继续清理其余窗口，再报告首个异常 |

> 效果持有来源的 `AttributeSet` 引用（用于按来源缩放数值）。
> 来源单位被回收时，该引用会让其属性集存活到效果被移除为止——这是有意的，
> 但对象池实现需要知道这一点。


## 本次依赖拆分验证

2026-10-26：纯 .NET 回归覆盖层级边界、多来源计数、激活/效果/冷却、取消与 Cue、事件重入与异常清理、名称及 DataContract 往返；预计算条件验证了 8,192 组状态组合，计数存储另做 20,000 次混合增删对照。

迁移后，19 组 .NET 回归、39 项隔离 Unity 工程集成断言通过，包含三个 `ZFramework.GAS` 程序集的实际名称验证。用例覆盖未知名称、required/granted 空行、序列化输入不变以及动画 Cue 的跳过和告警，保留已有加载边界过滤，查询热路径不增加检查。

Unity 6000.3 批处理编译和集成检查通过：Core 实际程序集不引用引擎或 Generated，Unity 代理序列化往返、旧 Prefab GUID 与内联名称、三个授权资产的定义构建、Tag Playground 均通过。Tags Editor 仍有 Unity 6 的旧 TreeView API 弃用警告。

预热后的激活/结束与标签查询、增删测得 0 字节分配。桌面 .NET 8 对照中完整激活/结束的单标签场景与原实现持平，8/32 标签场景更快。极小集合单独连续增删仍比旧字符串列表多出祖先计数维护成本；查询收益不能等同为所有工作负载都加速。新条件以只读引用暴露，避免每次查询复制条件结构。

### 重构前已有验证记录

以下保留既有验证记录，其覆盖范围和平台以各项说明为准；本次复验范围见上文。

- **500 余项行为断言**，全部在 Unity Editor 内真实执行（含真实 GameObject 与物理查询）：
  459 项运行期行为 + 10 组重入场景 + 6 条装箱检查 + 图结构 2 组 + 图编辑 46 项
  + Unity 层复查 13 项 + 核心层复查 23 项 + 端到端技能 19 项（真实播放模式）
- **热路径无可测量的托管分配**：核心库 10 条 + Unity 层 5 条热路径，
  六十万至百万级迭代下堆用量零增长，同批次阳性对照可稳定检出 25–28 B/次的分配。
  含每帧路径 `ProjectileTask.OnTick` / `AreaEffectTask.OnTick` / `GameplayCueRouter.Dispatch`
- **核心程序集依赖为系统程序集与 Tags Core；Unity 集成验证确认两者均不引用引擎或 Generated**
- 编译零警告
- **动画时间轴：27 项运行期断言**，在编辑器之外以确定步长手工 tick 执行
  （`AnimationTask` 及其依赖不触碰任何 UnityEngine 类型，因此可脱离 Editor 运行）：
  标记点升序触发、单帧跨多点不重不漏、非循环跑满即完成、循环每圈重触发且不自行结束、
  打断即 `Stop()` 并收尾区间、下达时长后标记点等比缩放、缺目标即抛异常、
  找不到片段仍推进时间轴且仍收到 `Advance` 与一次 `Stop`、
  区间起止各一次、`onFinished` 正确区分播完与打断。
  其中 `AnimationTask.OnTick` 在**每圈都走到标记点与区间**的 1000 帧里零分配，
  同批次阳性对照可检出分配；断言集本身经变异测试反证（故意破坏循环重置后确实报错）

### 本次编辑器重构验证（2026-10-26）

- Unity 6000.3.14f1 EditMode：**45/45**，覆盖命令及多节点分组 Undo/Redo、连接规则、链深度、子资产创建／删除／转换、深拷贝、稳定 ID、选择同步、真实指针拖拽写回与一步撤销、模板、校验和窗口节点保留。
- Unity PlayMode：Animator、Legacy、Sprite、官方 Spine 4.2 的播放／拉伸／Stop 恢复及标记时序；结果见 `Docs~/EditorRefactorStatus.md`。
- Core .NET 8：**19/19**。100,000 次预热激活／结束测得 **0 字节托管分配**。
- Unity Mono：时间轴每圈经过标签窗口及无分配测试生成器的任务事件，1,000 圈测得 **0 字节**；序列帧驱动预热 Play/Advance/Stop 测得 0 字节。该结果不覆盖内置 ProjectileSpawner / AreaEffectSpawner 创建新任务的分配。
- Runtime + Editor 对实际 Unity API 的额外编译检查：0 错误、0 警告。第三方 Tags / Spine Editor 在 Unity 6 存在弃用提示。

尚未执行两名新策划的正式可用性测试、五个样例的完整战斗端到端验收和 IL2CPP 构建。历史 46 项图编辑断言没有现成测试源可逐条迁移；本次可重复执行的测试源码在 `Tests~/Editor`、`Tests~/PlayMode`、`Tests~/Spine`。细节差异与未完成验收见 `Docs~/EditorRefactorStatus.md`。


### 测量的边界
- GC 测量在 **Editor + Mono** 下进行，**IL2CPP 未验证**——
  当前机器未安装 IL2CPP 构建支持模块，装上后一次构建即可判定（见 `REFACTOR.md` 31.6）。
  静态审计已做到元数据层面：源码零反射 API，两个运行期程序集**零自定义泛型、零 P/Invoke、
  零泛型虚方法**，所用 BCL 泛型的值类型实例化全部写在字段声明里、静态可见
- 检出下限取决于迭代次数：百万次约 2–3 B/次，六十万次约 25 B/次。
  因此结论是「无可测量的分配」而非「绝对 0 字节」
- **持续效果创建采用容器级复用**：外部只持有单调递增的 `ActiveGameplayEffectHandle`；
  内部 Active 状态在最外层操作结束后才移除列表槽位并回池，避免遍历失效、回调重入与 ABA；
  `Remove(handle)` 会立即令查询身份失效并撤销 Modifier、Granted Tag、Removed Cue 与条件效果，只有物理槽位删除和复用被延迟。
  删除时逐属性、逐标签完成结构清理，
  记录并最终重抛首个回调异常。仍被属性 Modifier 引用、无法确认完全脱钩的对象会永久丢弃而不是回池。
  全部 Modifier magnitude 会先解析到
  随池化对象按容量复用的缓冲区，成功后才发布到容器，因此解析失败不会留下半初始化效果或旧周期数值。
  池冷启动或解析缓冲区扩容时仍会分配，稳定容量后的同规模重复应用/移除不再为这两项持续分配。
- **其他非热路径仍有设计内分配**：`GameplayAbilitySpec`（每次授予）、每发投射物 / 每个区域的任务对象。

## 不包含的内容

- **具体表现**（粒子 / 音效 / 飘字）——经 Cue 路由由使用方实现
- **运行期图解释器**——效果与能力是数据资产（与 UE 一致），执行不经过图；
  编辑器里的技能结构图只是这些引用的可视化，不参与运行
- **召唤的具体实现**——涉及单位表、生成根节点、路径系统等游戏专属概念，
  派生 `GameplayAbilityTask` 即可，库不做只转发的壳
- **对象池**——视觉体与任务对象的生命周期归使用方
- **Live Link、Playable 与通用自定义事件轨道**——属于计划 M6 可选扩展。
