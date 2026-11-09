# ZFramework GAS

Unity 的 Gameplay Ability System 工具库：纯 C# 规则核心、可配置技能、动作流程、动画时间轴和运行时调试。

0.2 的边界是 **规则通用、执行显式、表现与 3D 战斗按程序集扩展、样例按需导入**。不内置职业、阵营、伤害公式、回合队列或网络协议。升级说明见 [0.2 重构与验证](Docs~/Refactor-0.2.md)。

## 功能归属

| 层 / 程序集 | 职责 | 接入场景 |
|---|---|---|
| `ZFramework.GAS` | ASC、属性、Effect、Ability、标签条件、事件、任务生命周期、Cue 路由 | 所有游戏；也可在 .NET 独立运行 |
| `ZFramework.GAS.Unity` | ScriptableObject 配置、参数、动作、授予/施放、目标过滤、服务注入 | Unity 通用技能创作 |
| `ZFramework.GAS.Unity.Realtime` | 动画时间轴、Section、Playables/Animator/序列帧、表现轨道、Cue VFX | 需要实时表现的游戏 |
| `ZFramework.GAS.Unity.Combat3D` | `PhysicsTargetSearcher`、武器碰撞、投射物、区域、Prefab 生命周期 | 使用 3D 物理与这些战斗模型的项目 |
| `ZFramework.GAS.Editor` | 文档、结构图、动作流程、参数编辑、严格校验、调试 | 不依赖 Realtime 或 Combat3D |
| `ZFramework.GAS.Editor.Realtime` | 时间轴编辑、动画预览、3D 几何预演 | 依赖 Realtime 与 Combat3D |
| `Runtime.Spine` / `Editor.Spine` | Spine 后端与预览接入 | 安装兼容 Spine 包后启用 |
| `Samples~/BasicCombat` | 训练场、阵营、伤害示例、具体技能和闪电连线 | 在 Package Manager 手动 Import |

这些是同一个 UPM 包里的独立程序集，不是多个独立发布的包。自定义 asmdef 只引用实际使用的程序集。Core 禁止引用引擎；Unity 基础层没有 `Physics`、`Collider` 或 `Rigidbody` 实现。

## 安装

Unity 最低版本保持 **6000.3**。在工程 `Packages/manifest.json` 中固定 GameplayTags 的来源，再安装 GAS：

```json
{
  "dependencies": {
    "com.acced.zframework.gameplaytags": "https://github.com/acced/ZFramework.GameplayTags.git#1.0.0",
    "com.acced.zframework.gas": "https://github.com/acced/ZFramework.GAS.git"
  }
}
```

本地开发也可使用 Package Manager 的 **Add package from disk** 选择本仓库 `package.json`。项目须先注册完整 GameplayTags 词表；热路径使用生成字段或缓存的句柄。

Basic Combat 不再默认编译。需要示例时在 Package Manager 的 Samples 中导入，再执行示例生成菜单。测试接入见 [Tests~/README.md](Tests~/README.md)。

## 纯 C# 核心

属性 ID 与具体规则由项目定义。ASC 不创建 GameObject，也不接管游戏循环。

```csharp
var mana = new AttributeId(1);
var owner = new AbilitySystemComponent();
owner.Attributes.Add(mana, 100);

var cost = new GameplayEffectDefinition(
    EffectDurationPolicy.Instant,
    modifiers: new[] {
        new EffectModifier(mana, ModifierOperation.Add, EffectMagnitude.Fixed(-20))
    });

var ability = owner.GrantAbility(new GameplayAbilityDefinition(cost: cost));
var result = owner.TryActivateAbility(ability);
owner.EndAbility(ability);
owner.Tick(deltaTime);
```

### 激活与提交

默认 `TryActivateAbility` 在激活时提交消耗与冷却。预瞄、蓄力或等待确认可显式延迟：

```csharp
if (owner.TryActivateAbility(ability, autoCommit: false) == AbilityActivationResult.Activated)
{
    int version = ability.ActivationVersion;
    // Save version with this aiming/charging operation.
    // A later confirmation commits this exact activation:
    var result = owner.CommitAbility(ability, version);
    if (result == AbilityActivationResult.Activated)
    {
        // Execute the confirmed gameplay operation.
    }
    owner.EndAbility(ability);
}
```

激活检查初始门禁；提交再次检查资源和冷却。同一次激活重复提交不会重复扣费。未提交前取消不付费。过期版本返回 `Inactive`，不能替后来一次施放付款。付款回调结束或撤销能力时，不再返回可继续启动任务的成功状态；已经支付的消耗与冷却不回滚。

Unity 技能资产的 **激活时提交消耗与冷却** 对应 `autoCommit`。关闭后，在动作流程的确认位置加入 **提交消耗与冷却**。提交失败会结束当前施放并停止后续动作。脱离能力的场景分支不能提交能力。

### Effect 与属性

- Definition 是共享的不可变配置；Spec 保存一次施加的来源/参数；ActiveEffect 保存持续状态和句柄。
- Definition 在构造边界复制 modifiers/conditionalEffects，公开 `ReadOnlySpan`，不会被调用方修改原数组污染。
- Instant 和 Periodic 修改 BaseValue；无周期的持续效果通过修改器推导 CurrentValue。
- Duration 周期效果只结算到到期时刻；大时间步不会在有效期之后补算伤害。
- 持续效果按定义引用识别堆叠。复用资产的 `Definition`，不要每次施加重新构造。
- 属性聚合采用本库的乘积乘除规则，不宣称逐项复刻 UE 的所有 Aggregator 语义。

### 任务与 Cue

能力拥有的 `GameplayAbilityTask` 在能力结束时取消。场景拥有的任务由显式 `GameplayTaskHost` 推进，可在施法者结束或回收后继续存在。两者复用同一套调度实现，以激活版本和运行版本区分复用对象。

Cue 只路由表现，GameplayEffect 负责数值和状态。没有全局 Cue 单例。网络同步、客户端预测和回滚由项目实现；库不提供未实现的占位接口。

## Unity 接入

### 通用或回合制目标

自身、指定对象、项目提供的目标名单不需要物理引擎，也不要求 Transform。

```csharp
var services = new SkillUnitServices();
services.Set<ISkillTargetSource>(boardTargets);
var runner = new SkillRunner(owner, services);
runner.Grant(skill, level: 3);
runner.TryCast(skill, new SkillCastTarget(selectedUnit));
```

`boardTargets` 实现 `ISkillTargetSource.Collect`，同步按优先级调用 `SkillTargetResults.Add`。公共查询层统一处理标签、关系过滤、身份去重、容量与数量上限。项目负责阵营、格子、射程和回合顺序。

### 3D 战斗与场景任务

物理查询、对象服务、场景任务由宿主显式选择。以下初始化执行一次：

```csharp
var sceneHost = gameObject.AddComponent<SceneSkillTaskHost>();
var services = new SkillUnitServices {
    Origin = transform,
    Scene = gameObject.scene,
    Searcher = new PhysicsTargetSearcher(),
    Objects = myObjectPool,
    SceneTasks = sceneHost.Tasks
};
var runner = new SkillRunner(owner, services);
```

`SceneSkillTaskHost` 是可选的 FixedUpdate 驱动组件；也可以直接创建 `GameplayTaskHost`，由项目统一 Tick。一个 host 只能由一个循环推进。`InstantiateSkillObjectService.Instance` 可用于原型，它会 Instantiate/Destroy；生产项目可注入对象池。

`PhysicsTargetSearcher` 使用配置场景的 `PhysicsScene`；投射物使用自身所在场景。缓冲区通过 searcher 创建：

```csharp
var searcher = new PhysicsTargetSearcher();
var buffer = searcher.CreateBuffer(128);
int count = searcher.Query(query, execution, buffer);
```

嵌套查询各自使用缓冲区。`Saturated` 表示候选空间被截断，不能把结果视为完整名单。扇形是 XZ 方向判定，属于此 3D 接入的明确规则。

### 动画接入

```csharp
var timeline = new SkillTimelinePlayer(runner, () => skillAnimator.Driver);
runner.Grant(skill);
runner.TryCast(skill, new SkillCastTarget(target, targetTransform));
// Dispose timeline when this runner is released.
```

动画时钟由 ASC.Tick 驱动，动画后端负责表现。时间轴可配置动作标记、标签窗口、Cue、命中窗口和 Section。详情见 [编辑器指南](Docs~/EditorGuide.md)。

## 创作与构建

打开 **Window → ZFramework GAS → 技能编辑器**。结构图描述资源关系，动作流程描述行为，时间轴描述时序；保留引用保护、Undo、共享模块、参数与运行时调试。

按 **F7** 或点击 **编译检查**，会重新构建定义缓存，执行全部规则并定位错误资产/动作。Player 构建使用同一套严格规则，不受个人关闭提示的设置影响。

构建校验覆盖工程内全部 GAS 资产，包含未引用草稿。未完成草稿需要修正或移到不被 Unity 导入的位置。检查包括定义参数、标签、模块循环、参数作用域、有效覆盖值和时间轴动作。

**当前编译产物是内存中的 Definition/ActionDefinition 缓存。** 首次 Grant 仍会构造这些缓存；Player 的后续 Cast 使用已准备内容，Editor 可刷新资产定义。本版本没有持久化 runtime-only Program、字节码 VM 或离线二进制导出。

## 性能合同与验证

正常执行约定主线程、有效配置、明确宿主与生命周期。不在每次施放重做配置预检，也不自动补授予能力、创建宿主或寻找对象服务。来源身份、激活版本、重入取消判断属于正确性约束。

`DefinitionSource` 的弱表与依赖失效图只在 Editor 存在。诊断通知只在 `UNITY_EDITOR`、`DEVELOPMENT_BUILD` 或显式 `GAS_DIAGNOSTICS` 下调用。

.NET 8 Release 回归覆盖标签、效果、重入、任务、提交与分配。已有暖机断言覆盖激活/结束、cost+cooldown+DoT、手动提交，测得 0 B。它不代表任意 Unity 动作树、首次构造、Prefab、投射物、目标分支或参数覆盖都无分配。没有执行 Unity Mono/IL2CPP/移动端测量。

```bash
python3 Tools~/verify_architecture.py
dotnet build Tests~/Core/Core.csproj -c Release -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet .build~/tests/bin/Release/net8.0/Core.dll
```

独立构建依赖相邻的 `../ZFramework.GameplayTags` 1.0.0 仓库。CI 固定其实际提交并执行上述检查。

设计边界和参考资料见 [RuntimeArchitecture](Docs~/RuntimeArchitecture.md)，破坏性变更与本次验证范围见 [Refactor-0.2](Docs~/Refactor-0.2.md)。
