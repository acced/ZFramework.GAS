# 最小接入样例

这里只有一个 `AbilityActor`，用于说明 ASC、技能授予、时间轴和 Tick 的所有权。输入、角色移动、阵营、投射物、对象池和战斗 UI 由项目提供。

1. 导入样例，将 `AbilityActor` 挂到两个 GameObject。
2. 用 `Window > ZFramework GAS > 技能编辑器` 创建技能；在技能的激活动作中添加「施加效果」。创建一个 Instant 效果，修改目标上已配置的 Attribute ID。
3. 将技能赋给施放者的 `abilities`。在目标的 `attributes` 中配置同一 Attribute ID 及初值。
4. 从项目的输入或 AI 调用 `source.Cast(skill, target)`，读取返回的 `AbilityActivationCheck`。目标无需动画或 Collider；空间查询需要 Collider。
5. 需要技能时间轴时，给技能配置 `SkillAnimationAsset`，直接选择 AnimationClip，并给 `AbilityActor.animator` 赋值。样例使用内置 Playables 驱动。

本例每个 actor 在 `Update` 中推进自己的 ASC；`OnDisable` 结束任务、移除效果并释放时间轴。使用池时，项目自行恢复属性初值。使用 GameplayTag 前，按 GameplayTags 包的要求完成标签注册。

若项目不需要 3D 物理查询，将 `PhysicsTargetSearcher` 改为 `TargetSearcher` 并移除样例 asmdef 的 Combat3D 引用。纯逻辑目标只需要 `SkillCastTarget`，无须任何 GameObject。
