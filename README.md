# ZFramework.GAS

Unity 技能工具库：纯 C# GAS 运行时、资产编排、技能时间轴与运行时调试。

本次重构以 **一种状态只有一个所有者、同一操作只有一个入口、游戏规则留给项目** 为边界。运行时不依赖主项目，也不内置角色、投射物、连招、伤害公式或装备系统。

## 功能边界

| 层 | 保留内容 | 依赖方向 |
| --- | --- | --- |
| Core | Attribute、GameplayTag、Effect、Ability、Task、Event、Cue、不可变参数 | 仅 GameplayTags；不引用 Unity |
| Unity | 能力/效果资产、动作编排、SkillRunner、逻辑目标与项目服务 | Core |
| Realtime | 单个 AnimationClip 时间轴、Marker、Tag/Cue 窗口、命中窗口、音效、Playables 驱动 | Unity + Core |
| Combat3D | 3D 查询适配、碰撞体槽位与几何 | Unity + Core |
| Editor | 一个技能编辑器、一个运行时调试窗口 | Unity + Core |
| Editor.Realtime | 时间轴编辑、姿势/VFX/音效与查询范围预览 | Editor + Realtime + Combat3D |
| Samples~ | 一个可选 AbilityActor 接入样例 | 仅用户导入时使用 |

`Combat3D` 当前是程序集边界，尚未拆成单独发布的 UPM 包。阵营判断和逻辑目标来源通过项目服务注入；框架不定义阵营、角色类型或敌我规则。

### 保留的内置动作

- 提交消耗与冷却：`CommitAbilityAction`。
- 施加效果：`ApplyEffectAction`。
- 标签、属性和 Bool 参数条件分支：`ConditionalAction`。
- 发送玩法事件：`SendGameplayEventAction`。
- 对查询到的每个目标执行分支：`SelectTargetsAction`。
- 延时、等待事件或等待目标输入：`WaitSkillAction`。

项目行为通过 `SkillAction` / `GameplayAbilityTask` 扩展。修改器统一由 Effects 管理，不开放绕过效果生命周期的属性 modifier 写入口。

## 安装与要求

Unity 6000.3；GameplayTags 1.0.0。通过 Package Manager 的 Git URL 安装：

```text
https://github.com/acced/ZFramework.GAS.git
```

当前重构工作在 `refactor/gas-library-boundaries` 分支；检查该分支时在 URL 后添加 `#refactor/gas-library-boundaries`。先配置 GameplayTags 包的来源与标签注册。样例通过 Package Manager 的 Samples 导入。

## 一个施放入口

```csharp
using ZFramework.GAS;
using ZFramework.GAS.Unity;

var owner = new AbilitySystemComponent();
owner.Attributes.Add(new AttributeId(1), 100);

var runner = new SkillRunner(owner, new SkillUnitServices {
    Origin = transform,
    Searcher = new PhysicsTargetSearcher(), // 空间查询显式接入
    Scene = gameObject.scene
});
var spec = runner.Grant(skill, level: 1);

var check = runner.TryCast(skill, new SkillCastTarget(targetAsc, targetTransform));
if (!check.Succeeded)
    UnityEngine.Debug.Log(check.Message);

// 主项目决定 Update、FixedUpdate 或自己的模拟时钟。
owner.Tick(deltaTime);

// 状态与取消直接使用同一份 spec。
bool active = spec.IsActive;
float remaining = spec.CooldownRemaining;
owner.EndAbility(spec);
runner.Revoke(skill);
```

纯逻辑目标可使用 `new SkillCastTarget(targetAsc)`，并省略物理 Searcher、Transform 和 Scene。调用 `Clear()` 后，旧授权失效；再次使用前显式 `Grant`。

### 参数先解析，施放时复用

参数在技能资产自身声明。需要动态数值时，在配置或输入变化时生成不可变上下文；`TryCast` 不缓存可写覆盖数组。

```csharp
var values = new EffectEvaluationContext(
    level: 3,
    parameters: skill.ResolveParameters(3, new[] {
        new SkillParameterOverride { parameterId = damageParameterId, value = 12f }
    }));

// 同一份 values 可以用于后续多次施放。
var check = runner.TryCast(skill, new SkillCastTarget(targetAsc), values);
```

所有动态效果数值通过 `EffectMagnitude.FromParameter` 读取同一份强类型参数。`SkillParameterOverride[]` 仅是解析输入；解析后修改它不会改变已经生成的上下文。项目伤害算法通过 `IEffectCalculation` 实现。

### 时间轴只有一个绑定入口

```csharp
using var presentation = new SkillTimelinePlayer(
    runner, new PlayableAnimationDriver(animator));

// 仍然只调用 runner.TryCast。presentation 不提供另一套 Cast API。
```

技能表现资产直接引用 AnimationClip。时长由时间轴定义，GAS Tick 推进玩法边界；Playables 接收归一化位置。结束单位前先结束 ASC 工作并释放 presentation。项目自己的动画系统可以实现 `IAnimationDriver`；库只附带 Playables 实现。

### 显式提交

`autoCommit = true` 在激活时支付消耗并进入冷却。设为 false 后，在动作列表或 Marker 中放置「提交消耗与冷却」。异步代码提交必须携带本次版本：

```csharp
var check = owner.CommitAbility(spec, capturedActivationVersion);
```

取消尚未提交的施放不会支付；已发生的支付不会自动退还。`TryActivateAbility`、`CommitAbility`、`TryCast` 直接返回 `AbilityActivationCheck`，不在 Spec 上重复保存历史结果。

## 编辑器

仅保留两个菜单：

- `Window > ZFramework GAS > 技能编辑器`：技能库、关系图、动作分支、详情、时间轴与预览。
- `Window > ZFramework GAS > 运行时调试`：当前 ASC 的属性、标签、效果、能力和任务。

主窗口新建空白技能，用效果资产表达消耗、冷却和效果；F7 执行编译检查，问题可定位到字段或动作。构建前校验会检查全部 GAS 资产，包括未被技能引用的效果。这里的编译检查构建并校验内存定义，不输出离线字节码。

## 性能与生命周期

- Player 在 Grant/准备阶段构建并缓存定义与动作，施放不扫描资产或使用反射。
- Core 的任务和效果支持取消、清空、对象池代际与回调重入。
- 查询缓冲复用；结果携带目标 generation，项目过滤回调前后检查身份。
- Editor 的资源反查与调试通知不进入普通发布版热路径。
- 没有全局动画调度器、隐藏场景 TaskHost、模块解释器或可变参数父链。

Core 的热身分配回归不代表所有 Unity 操作零分配：创建 Playable、分支上下文、自定义任务、VFX 实例以及冷启动参数解析应在实际项目中测量。

## 验证与文档

CI 验证程序集边界并构建、运行 .NET Core 回归。Unity EditMode/PlayMode 测试保留在 `Tests~`，需要真实 Unity 工程执行；当前环境没有 Unity，未宣称它们已通过。

- [架构与所有权](Docs~/RuntimeArchitecture.md)
- [编辑器使用](Docs~/EditorGuide.md)
- [程序集边界](Docs~/EditorAssemblyBoundaries.md)
- [删除清单与迁移](Docs~/Refactor-0.2.md)
- [验证说明](Tests~/README.md)

设计参考：[UE GAS](https://dev.epicgames.com/documentation/en-us/unreal-engine/understanding-the-unreal-engine-gameplay-ability-system)、[UniTask](https://github.com/Cysharp/UniTask)、[UniText](https://github.com/LightSideKittens/UniText)、[Luban](https://github.com/focus-creative-games/luban)。采用明确生命周期、不可变共享定义、准备阶段与热执行阶段分离等原则；这些库没有被加入运行时依赖。
