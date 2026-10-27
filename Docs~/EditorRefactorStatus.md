# 技能编辑器重构交付记录

日期：2026-10-26。依据 `EditorRefactorPlan.html`，本次实现 M0–M5 主线，M6 可选扩展未实施。原计划文档未修改。

## 已落地

- M0：共享 SkillDocument、序列化对象池、ChangeSet、SelectionModel、PlaybackModel、命令分组、稳定时间轴 ID 和迁移菜单；UXML/USS 三栏工作台。
- M1：增量结构图、类型化端口规则、搜索创建、内嵌资产、深拷贝、删除、内嵌／外置转换、只读 Cue 节点、布局与注释、小地图、Details 和能力门禁模拟器。
- M2：UI Toolkit 多轨时间轴、片段选择、姿态定位、播放、变速虚影、缩放平移、帧吸附、框选与多选移动、复制粘贴、区间手柄、稳定 ID 选中联动。
- M3：隔离预览场景、旋转缩放、Cue 文字与 ICuePreview；校验消息、定位及适用的修复；技能库、五种模板和 BasicCombat 资产生成器；旧入口兼容重定向。
- M4：SkillAnimator、Animator/Legacy/Sprite 后端、SerializeReference 策略选择、可选 Spine Runtime/Editor 程序集。
- M5：标签窗口及回滚、任务事件、ProjectileSpawner/AreaEffectSpawner 和宿主服务上下文；边界按时间排序处理，支持取消重入与异常清理。

核心 Core 语义和既有序列化字段名保留。旧三个编辑器实现已替换，SkillGraphWindow 保留静态兼容入口。没有提交 Git commit。

## 已执行的验证

| 环境 | 结果 |
|---|---|
| Core .NET 8 | 19/19，100,000 次预热激活／结束 0 allocated bytes |
| Unity 6000.3.14f1 EditMode，最终代码 | 45/45 |
| Unity PlayMode，最终代码 | 4/4：Animator、Legacy、Sprite、官方 Spine 4.2 |
| 无 Spine 条件程序集验证，先前回归轮次 | 36/36；可选程序集被排除 |
| Runtime + Editor 对实际 Unity API 的额外编译 | 0 errors，0 warnings |
| Unity Mono 时间轴窗口＋无分配任务生成器 | 1,000 圈 0 bytes；不代表每次创建内置任务都无分配 |

EditMode 包括真实 PointerDown/Move/Up 时间轴拖拽测试：拖动期间序列化数据不变，抬手写回，一次 Undo/Redo 往返；包括链深度的上游路径、Inspector 重复 ID 修复、验证模式不改 ID、跨面板选择、子资产外置及 Undo/Redo、深拷贝和多节点分组。

本次没有将历史 README 中缺少源测试的“46 项图编辑断言”冒充为重新执行。官方 Spine 4.2.120 / spine-csharp 4.2.37 已实测；Spine 4.0/4.1 尚未逐版本测试。第三方 Tags / Spine Editor 有 Unity 6 API 弃用提示。

测试源码在 `Tests~/Editor`、`Tests~/PlayMode`、`Tests~/Spine`，可链接到测试项目 Assets 后由 Test Runner 运行。临时验证日志及 XML 位于被 Git 忽略的 `.build~/`。

## 审查修复（2026-10-27）

| 问题 | 修复 | 回归测试 |
|---|---|---|
| 循环且被拉伸时，「随动画结束」的区间/窗口在圈末收不掉，标签计数泄漏、Cue 重复开启（authored × scale 浮点多出一个 ulp，如授权 0.8s 拉到 0.44s） | `AnimationTask` 构造时一次换算缩放时间并夹到 `Duration`；末尾标记点也因此逐圈触发 | `ScaledLoopClosesEndWithAnimationSpansEveryCycle`（三组溢出组合；移除修复后三组均失败） |
| GAS002 只检查标签名非空，未注册的冷却标签让运行期构造能力定义抛异常 | 标签全部未注册时报错误并说明后果；连线规则保持只要求非空 | `CooldownWithOnlyUnregisteredTagsIsAnError` |
| Details 重命名主资产只改 `m_Name`，名称与文件不一致 | 主资产走 `AssetDatabase.RenameAsset`；`LayoutStore` 改为保存时按资产取路径 | `LayoutSavesToRenamedRootAsset` |
| 「引用项目效果」列出其他技能的内嵌效果 | 过滤其他文件的子资产，保留当前技能的内嵌效果 | `ReferenceCandidatesExcludeOtherSkillsEmbeddedEffects` |
| 同时删除节点和无关连线时，连线删除被忽略 | 同一 Undo 事务内处理节点与两端都保留的连线 | `DeletingNodeAndUnrelatedEdgeTogetherAppliesBothInOneUndo` |
| 播放头显隐读取未更新的 `resolvedStyle` | 用本次计算的位置判断 | 无专项测试 |

修复后验证：Core 19/19；Unity 6000.3.14f1 EditMode 52/52；PlayMode 4/4（含官方 Spine 4.2）。

## 使用与测试工程

- 用户工程包链接：`C:/Users/pc/Testproject/Packages/com.acced.zframework.gas` → 当前仓库。
- 独立验证工程：`C:/Users/pc/Testproject/GASRefactorValidation~`。Spine 4.2 只用于该工程，未升级原工程的 Spine 3.8。
- 窗口入口：`Window → ZFramework GAS → 技能编辑器`，也可双击能力资产。
- 示例入口：`Tools → ZFramework GAS → 创建 BasicCombat 示例`；标签注册和宿主配置见 `Samples/BasicCombat/README.md`。
- 操作与接入说明见 `README.md` 及 `Docs~/EditorGuide.md`。

## 与计划的差异及未完成验收

- 本次是主体功能落地，尚未达到计划全部逐项验收：未组织两名新策划的可用性测试，未录制三个完整工作流，未完成五个示例技能在完整战斗宿主中的端到端比对。
- 图的双击连线重路由点、折叠状态持久化、完整节点外观注册扩展尚未实现；布局采用有界分层排布，并非完整 Sugiyama 优化算法。
- 节点保留已测试；100 节点交互和 500 技能索引性能没有专项测量。技能索引使用项目变更失效缓存，未做逐资源增量 AssetPostprocessor。
- 动画按 SkillAnimator 配置层枚举；BlendTree 降级为首个子片段。预览尚无地面网格和目标假人，Cue 默认文字提示。
- 所有需要用户选择的校验问题提供定位；仅可确定修复方式的项提供自动修复。不是每一种错误都能无需用户输入一键修复。
- 节点和时间轴业务数据操作支持 Undo；纯布局元数据没有完整 Undo 历史。新建／复制整个资产文件遵循 AssetDatabase 文件操作语义。
- 本次样例为资产和预览角色生成器，未恢复历史文档描述但仓库中缺失的完整 CombatArena/CombatUnit 战斗实现。
- IL2CPP、Spine 3.8 适配、Live Link、Playable、通用运行期自定义轨道未做。

Unity 实际窗口已启动并进行视觉检查，修复了搜索框溢出等问题。当前桌面为 1024×768，截图曾被系统注销倒计时提示遮挡；不将该截图当作完整视觉验收凭据。

