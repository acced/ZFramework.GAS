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

### 接一个 Spine 适配器

Spine 未安装在本项目里，**库内不留未经编译验证的代码**。装上 `spine-unity` 后照抄这一份：

```csharp
using Spine.Unity;
using ZFramework.GAS.Unity;

public sealed class SpineAnimationDriver : IAnimationDriver
{
    private const int Track = 0;
    private readonly SkeletonAnimation _skeleton;

    public SpineAnimationDriver(SkeletonAnimation skeleton) => _skeleton = skeleton;

    public float Play(string clipName, float duration, bool loop)
    {
        Spine.Animation clip = _skeleton.Skeleton.Data.FindAnimation(clipName);
        if (clip == null)
        {
            return 0f;                                  // 与其余驱动一致：找不到片段返回 0
        }

        Spine.TrackEntry entry = _skeleton.AnimationState.SetAnimation(Track, clip, loop);
        bool stretch = duration > 0f && clip.Duration > 0f;
        entry.TimeScale = stretch ? clip.Duration / duration : 1f;   // 拉伸到下达的时长
        entry.TrackTime = 0f;
        return stretch ? duration : clip.Duration;
    }

    public void Advance(float normalizedTime)
    {
        // Spine 自行推进，与 Animator / 旧版 Animation 同理。
    }

    public void Stop()
    {
        if (_skeleton != null)
        {
            _skeleton.AnimationState.SetEmptyAnimation(Track, mixDuration: 0.1f);
        }
    }
}
```

## 编辑器：技能结构图

`Window/ZFramework GAS/技能结构图`，或直接双击一个 `GameplayAbilityAsset`。

图**不是新的数据格式**：节点就是资产，连线就是资产之间已有的引用（消耗 / 冷却 / 子效果）。
因此没有「图数据」与「运行期数据」两份来源，也不存在编译或导出步骤——
在图上改完，就是改完了资产本身。

| 操作 | 效果 |
|---|---|
| 从端口拖出连线接到另一个节点 | 写回对应引用 |
| 从 Project 面板把效果资产拖到端口上 | 同上，省去先建资产再回 Inspector 引用的往返；多选拖拽会逐个挂载 |
| 从 Project 面板把能力资产拖到空白处 | 切换根节点 |
| 选中连线按 Delete | 清除该引用（消耗 / 冷却置空，子效果移出数组） |
| 双击节点 | 在 Project 面板中定位并选中该资产 |
| 右键 → 新建效果资产 | 在根能力所在文件夹创建并选中，随后拖到端口上挂载 |

端口是**有类型的**：能力的「消耗」「冷却」只收效果资产，「动画」只收动画资产，
拖不出没有意义的连线，也就不会出现「连上了却被静默丢弃」。
动画节点上每个标记点各一个输出端口（含没挂效果的——空端口正是「把效果拖到这一下」的落点），
端口名里的下标是标记点在图上的稳定标识，名字只是给人看的，可空可重复。

**节点本身不可删除**。节点即资产，删节点没有对应的数据语义；
若放任删除，GraphView 会把该节点的所有连边一并交给变更回调，
于是「从视图上移掉一个框」会静默清空这个资产的进出引用——删根节点更是直接抹掉能力的消耗与冷却。
图上唯一有意义的删除是删一条连线。

所有写入都走 `SerializedObject.ApplyModifiedProperties()`，因此 Ctrl+Z 与 Inspector 编辑同源。
每次写回后整图按数据重建——视图始终是数据的投影，不持有独立状态。

四条约束在**授权期**就拦截，并弹窗说明原因，不必等到运行期才发现配错：

- 消耗必须是 `Instant` 效果
- 冷却效果必须至少授予一个标签（那个标签就是「在冷却中」的标志）
- 效果不能把自己挂为子效果
- 同一子效果不能在同一触发时机重复挂载（会在运行期施加两遍）；换个触发时机则允许

多处引用同一效果时复用同一个节点，连线自然汇聚；成环也因此不会让展开死循环。

**没有的功能**：节点位置不持久化（每次按引用结构重新布局）。
保存位置需要引入一份图专属数据，那就回到了「两份来源」，与上面的设计相斥。

### 底部的动画时间轴

根能力挂了动画资产时，窗口底部展开一条时间轴。它只管一件事：**时间**。
标记点的名字与效果、Cue 标签、作用目标都是普通字段，Inspector 的列表编辑已经够用；
唯有「这一下落在动画的哪个位置」必须看着刻度拖。

| 操作 | 效果 |
|---|---|
| 拖动标记点头 | 改时间点。按帧吸附（用资产上的 `frameRate`），按住 Alt 走自由值 |
| 拖动区间左右手柄 | 改起止；终点为负的区间没有右手柄——它的右端由动画时长决定，不是一个可拖的时间 |
| 拖动区间中段 | 整体平移，长度不变 |
| 右键空白 | 在光标处新增标记点 / 新增区间 |
| 右键标记点或区间 | 删除；区间还可把终点改为「随动画结束」 |

**拖拽期间不写序列化数据**，只改临时字段；抬手时一次性 `ApplyModifiedProperties()`——
因此一次拖拽正好一步撤销，也不会触发「写回 → 整图重建 → 把正在拖的东西换掉」。
超出动画时长的标记点画成红色：它永远不会触发，是配置错误而非「暂时禁用」。

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

`Samples/BasicCombat/`（518 行，独立 asmdef）用本库搭出四个代表性技能，
**没有为跑通它们给库加任何代码**：

| 技能 | 用到的机制 |
|---|---|
| 普攻 | Instant 效果 + 消耗 + 冷却 + 标签门禁 + Cue |
| 普攻（带动画） | 同上，外加动画时间轴：伤害落在第 0.35s 的标记点，剑光由 Cue 区间起止，播完即结束能力 |
| 狂暴 Buff | Duration + 授予标签 + 按来源堆叠 + 子效果链 + 周期回复 |
| 投射物 | AbilityTask + 物理查询 + 命中效果，能力保持激活直到命中 |
| 召唤 | 派生 AbilityTask（96 行），打断即回收 |

带动画的普攻与瞬发版本**共用同一个能力定义**：门禁、消耗、冷却都不变，
变的只是「什么时候结算」从「立刻」变成「挥到那一下」。

除带动画的普攻外，其余四条路径均已在真实播放模式下验证过
（动画那条的现状见下方「已验证的事实」）。

范例同时演示了两件宿主该做的事：`GameplayCueRouter` 由 `CombatArena` 持有并在构造 ASC 时传入
（一场战斗一个，不是单例）；`CombatUnit` 在 `FixedUpdate` 里用 `Time.fixedDeltaTime` 推进
（固定步长，战斗逻辑确定可复现）。

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

### 动画时间轴尚未验证的部分
- **三个驱动适配器**（Animator / 旧版 Animation / 序列帧）需要真实组件，未在播放模式下跑过
- **带动画的普攻**的端到端（真实播放模式下断言伤害落在预期帧、Cue 起止各一次）未跑
- **编辑器断言**（动画端口与标记点端口的连线 / 断开 / 撤销、时间轴拖拽写回与一步撤销）未跑
- GC 数字来自 .NET 6 上的同一份 IL，**未在 Unity 的 Mono 下复测**

上述四项都需要 Unity 编辑器在位；本轮环境没有，因此如实标为未验证，而非按「应该没问题」记为通过。

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
- **Spine 适配器**——Spine 未安装在本项目，库内不留未经编译验证的代码；
  上面「接一个 Spine 适配器」给了完整可抄的实现
- **动画预览与 scrub**——时间轴只编辑时间，不渲染动画预览
