# BasicCombat

在 Unity 菜单选择 **Tools → ZFramework GAS → 创建 BasicCombat 示例**。
示例会生成到 Assets/GAS Samples/BasicCombat，包含五种模板、属性词表、
一个程序生成的剑士预制体、Animator Controller 与 0.8 秒的挥剑动作。
再次运行不会覆盖已有技能。

普攻包含伤害、体力消耗、冷却、刀光 Cue、霸体及连招窗口。
在项目 Gameplay Tags 词表中声明 Cooldown.普攻_剑士、Cooldown.投射物、
Cooldown.狂暴 Buff、Cue.Sword.Trail、State.SuperArmor、Window.Combo 后重新生成词表。
示例属性 ID 为生命 1、最大生命 2、攻击 3、体力 4；已有项目请按自己的词表调整。

动画预览不执行战斗规则。运行期由宿主创建 ASC，添加属性、授权能力并推进 Tick，
再通过 AnimationTask 与 SkillAnimator.Driver 启动动画任务。
投射物模板需配置视觉预制体，并提供 TimelineTaskContext 的对象池和目标查询服务。
召唤模板保留纯回调事件，实际生成单位由宿主实现。
