# 运行时架构：执行链概览

本文说明技能从施放到结束的完整数据流，面向需要扩展或调试框架的开发者。

## 核心对象关系

```
AbilitySystemComponent  (实体门面)
 ├── AttributeSet        (属性)
 ├── OwnedTags           (标签计数容器)
 ├── GameplayEffectContainer (生效效果)
 ├── GameplayEventChannel    (玩法事件)
 └── Tasks[]             (运行中的 GameplayAbilityTask)

SkillRunner  (技能授予与施放入口)
 ├── Grant / TryCast
 ├── Entry per skill
 │    ├── GameplayAbilitySpec  (授予实例)
 │    ├── Prepared             (缓存的参数与上下文)
 │    └── SkillActionDefinition[] (Build 阶段产物)
 └── ExecutionLifetimeTask     (追踪施放结束)

SkillExecutionContext  (每次施放的可变状态)
 ├── CastState (共享：Asset, Ability, Buffers, Channels, Records)
 ├── Branch    (值拷贝：Target, Position, Forward, HitPoint, Event)
 ├── Values    (不可变 EffectEvaluationContext)
 └── Services  (TimelineTaskContext → SkillUnitServices)
```

## 施放流程

### 1. 授予 (Grant)
```
SkillRunner.Grant(asset)
  → EnsureSpec: ASC.GrantAbility 创建 GameplayAbilitySpec
  → asset.BuildActions(): 所有 SkillAction.Build() 运行一次，
    产出不可变的 SkillActionDefinition[] 树
  → Prepare: 解析参数，缓存 EffectEvaluationContext
```

### 2. 施放 (TryCast)
```
SkillRunner.TryCast(asset, target)
  → ASC.TryActivateAbility(spec, values)
      标签门禁 → 冷却检查 → 消耗检查 → 扣消耗/上冷却/授予激活标签
  → new SkillExecutionContext(asset, spec, target, values, services)
  → context.Execute(actions)  // 同步执行动作树
  → new AnimationTask(definition, driver) // 可选：启动时间轴
  → ExecutionLifetimeTask 追踪所有任务完成
```

### 3. 动作执行

```
SkillExecutionContext.Execute(actions)
  for each SkillActionDefinition:
    if !CanContinue → 停止
    action.Execute(context)
      └── 每个动作可以：
          ├── 施加效果 (ApplyEffectAction → ASC.Effects.Apply)
          ├── 发事件 (SendGameplayEventAction → ASC.Events.Send)
          ├── 条件分支 (ConditionalAction → Execute yes/no)
          ├── 选择目标 (SelectTargetsAction → Searcher.Query)
          ├── 生成对象 (SpawnPrefabAction → context.Start task)
          ├── 发射投射物 (LaunchProjectileAction → context.Start task)
          ├── 创建区域 (CreateAreaAction → context.Start task)
          ├── 等待 (WaitSkillAction → context.Start task)
          └── 调用模块 (CallModuleAction → context.ExecuteModule)
```

动作可以启动 `GameplayAbilityTask`，这些任务挂在 ASC 上，随能力取消而取消。
标记为 `SkillTaskOwnership.Scene` 的任务挂在场景 `GameplayTaskHost` 上，独立于施放生命周期。

### 4. 时间轴 (AnimationTask)

```
AnimationTask.OnTick(deltaTime)
  → ProcessBoundaries(head)
      到达标记点 → 执行 marker.Actions 或回调 onMarker
      到达标签窗口 → AddTag / RemoveTag
      到达 Cue 区间 → Dispatch OnActive / Removed
      到达运行时轨道 → handler.Enter / Sample / Exit
  → driver.Advance(normalizedTime)
  → 循环/段落跳转/结束
```

### 5. 结束

```
能力结束时 (EndAbility 或 ExecutionLifetimeTask.FinishIfIdle)
  → ASC.DeactivateInternal(spec)
      移除激活标签
      取消该能力版本的所有任务
  → AnimationTask.OnEnd → driver.Stop, 关闭 Cue/标签窗口
```

## 关键不变量

| 规则 | 位置 |
|------|------|
| Build 阶段验证配置，Execute 阶段不验证配置 | SkillActionBuildContext |
| SkillActionDefinition 不可变，可跨施放复用 | SkillRunner.Prepare |
| 每次施放的可变状态全部在 SkillExecutionContext | SkillExecutionContext |
| Branch 是值拷贝：选目标不影响兄弟分支 | SkillExecutionContext.Branch struct |
| TargetQueryBuffer 来自 CastState 的栈式池 | RentBuffer / ReturnBuffer |
| CanContinue = 当前施放仍活跃 或 已脱离 (detached) | SkillExecutionContext.CanContinue |
| 版本号 (ActivationVersion) 防止跨施放的任务误操作 | GameplayAbilitySpec / AnimationTask |

## 扩展点

- **自定义动作**：继承 `SkillAction`，实现 `Build` 返回 `SkillActionDefinition`
- **自定义运行时轨道**：实现 `IRuntimeTrackHandler`，在时间轴区间内采样
- **动画后端**：继承 `AnimationBackend`，返回 `IAnimationDriver` 实现
- **目标过滤**：实现 `ITargetFilter` 或 `ITargetFilterSnapshot`
- **场景服务**：通过 `SkillUnitServices` 注入 Audio/Camera/Time/Rig/Objects
