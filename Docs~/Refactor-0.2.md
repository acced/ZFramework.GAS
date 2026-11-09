# 0.2 重构与验证

基线：`e84db59bae134d09ac771bfe0f77d89e9a06cc88`。本次修改保留已有技能文档、动作流程、时间轴与调试工具，重点修正核心生命周期、程序集职责和发布检查。

## 实际改动

| 问题 | 修改 | 结果 |
|---|---|---|
| 激活立即扣费，不能表达确认前免费取消 | 增加 autoCommit、CommitAbility、激活版本和 IsCommitted | 默认保持立即提交，瞄准/蓄力可延迟 |
| 支付回调结束能力后仍报告成功 | 提交结束检查当前激活；同步 LastCheck | 不向已结束能力启动任务；已支付部分不退款 |
| Duration DoT 使用完整大步长结算 | 周期累计时间截到剩余有效期 | 不在到期之后额外伤害 |
| Definition 暴露调用方可修改的数组 | 构造时复制、ReadOnlySpan 访问 | 跨单位共享配置不会被原数组污染 |
| ASC 与外部宿主重复调度代码 | 复用 GameplayTaskHost | 一套任务快照、版本、取消语义 |
| Effects.Count 每次扫描；无删除也扫描清理 | 显式待移除计数 | Count O(1)，按需清理 |
| modifier/tag 回调中提前移除会继续发布 | 在发布过程维护实际所有权并停止后续安装 | 不遗留标签或属性 modifier |
| 新替代效果误匹配自身的移除规则 | 在发布新实例之前移除旧效果 | 替代效果保留 |
| Cue 派发中 Clear/Register 破坏遍历 | 延迟压缩、保持派发边界 | 新注册项从下次派发起执行 |
| 基础选敌直接使用 Physics/Collider | TargetSearcher 策略 + Combat3D | 基础 Unity 无物理类型依赖 |
| 物理查询使用默认世界 | 使用指定/实例所在 PhysicsScene | 支持独立物理场景 |
| 基础上下文默认补齐对象服务、自动创建场景 GO | 删除隐式兜底，显式注入 | 项目控制实例化、时钟与释放 |
| 发布包每次 Cast 走配置失效路径 | Player 在 Grant 准备；热刷新仅 Editor | 正常 Cast 只使用准备结果 |
| 发布包维护 Editor 来源弱表和依赖图 | UNITY_EDITOR 隔离 | Player 不建立作者依赖图 |
| 诊断回调进入普通发布路径 | Conditional 宏裁剪通知 | 开发诊断可用，Release 无通知调用 |
| 编辑器提示不能阻止错误进包 | F7/编译检查、Player 强制门禁 | 同一语义规则，不能用个人设置绕过 |
| 样例和硬编码闪电逻辑默认进入库 | Samples~ + 样例预览注册 | 包安装不编译训练场和游戏规则 |
| 无用窗口兼容入口 | 删除 SkillGraphWindow 和重复菜单 | 一个明确的编辑器入口 |

## 最小性统计

口径为 C# 文件行数，含注释与空行；它说明代码归属和规模，不证明运行速度。

| 范围 | 基线 | 本次 |
|---|---:|---:|
| 纯 C# Core | 2,777 | 2,756 |
| Unity 基础程序集 | 2,109 | 1,985 |
| 默认导入的生产 C#，含原来默认导入的样例 | 19,889 | 16,592 |
| BasicCombat 样例 | 默认编译，3,429 行 | 按需导入，3,484 行 |

新增提交语义、构建门禁和物理适配没有引入泛用 VM 或平行执行框架。默认导入代码减少 3,297 行，主要来自将 3,429 行原样例职责移出默认安装。

## 迁移

### 1. 3D 查询

引用 `ZFramework.GAS.Unity.Combat3D`，把物理场景使用的 `new TargetSearcher(...)` 改为 `new PhysicsTargetSearcher()`。通过 `searcher.CreateBuffer(capacity)` 获取缓冲区。以前构造函数的 maxCandidates 参数实际上未使用；现在容量由缓冲区明确决定。

基础 `TargetSearcher` 继续支持 Self / FixedTarget / Project。没有注入空间实现时使用几何查询会报配置错误，不会暗中选用 3D 物理。

### 2. 宿主与服务

显式配置 `SkillUnitServices.Objects` 和 `SceneTasks`。库不再在缺失配置时创建隐藏宿主或选择 Instantiate。需要默认实现时主动传 `InstantiateSkillObjectService.Instance`，需要 FixedUpdate 时主动添加 `SceneSkillTaskHost` 并传入其 Tasks。

`SceneSkillTaskHost.For(scene)` 已删除。场景宿主由创建它的场景/项目拥有。

### 3. 授予

ASC.Clear 会撤销授予；后续 Cast 不自动重建 Spec。重新初始化时显式调用 `runner.Grant(skill)` 或 `abilitySet.GrantTo(runner)`。撤销来源仍只撤销对应套组/直接授予的贡献。

### 4. 命名与配置

- `TimelineTaskContext` 改名为 `SkillExecutionServices`，它服务于任意动作分支，不仅动画。
- AnimationTagWindow 相关类型移到 Realtime；基础层只持有通用服务与目标输入。
- Effect Definition 的 Modifiers / ConditionalEffects 现在是只读 Span；需要列表持久副本时由调用方显式复制。
- `autoCommit` 默认 true；要延迟扣费，关闭并加入提交动作。
- `SkillGraphWindow` 已删除；使用 `SkillEditorWindow.Open()`/现有技能菜单。
- 诊断订阅在普通发布构建不会收到通知；需要发布诊断时定义 `GAS_DIAGNOSTICS`。

### 5. 样例与序列化

BasicCombat 从 Samples 移到 Samples~，通过 Package Manager Import 才进入工程。ShowTargetLinkAction 归属样例程序集；依赖它的旧技能需要先导入样例，或改成项目自己的表现动作。

移动的 MonoBehaviour 脚本保留原 meta GUID。跨程序集移动的 SpawnPrefabAction、LaunchProjectileAction、CreateAreaAction 和 ShowTargetLinkAction 带有 MovedFrom 元数据，供 Unity 迁移托管引用。该迁移尚未在实际 Unity 工程中验证，升级已有资产时仍须检查托管引用与引用缺失列表。

### 6. 严格构建

Player 构建检查工程内全部 GAS 资产，含未引用草稿；不是仅检查当前场景。编辑器的禁用规则偏好只影响日常提示，F7 和构建不能绕过。先修复配置，再构建。

## 验证记录

验证环境：Linux x64、.NET SDK 8.0.416 / Runtime 8.0.22、GameplayTags 1.0.0 对应提交 `8dde1a43485f33031fd73b4e6cb21304f03c19df`。

### 已执行

- Release 编译：0 warnings，0 errors。
- Core：34 个测试组全部通过。
- 10,000 次 cost / cooldown / DoT / 变化来源属性循环：暖机后 0 B。
- 10,000 次手动激活 / 提交 / 冷却循环：暖机后 0 B。
- 100,000 次激活 / 结束循环：暖机后 0 B；一次本机测量 64.82 ms。该时间不是跨项目性能对比。
- 静态程序集检查：Core 无 Unity 依赖、基础 Unity 无物理类型、本地依赖无环、Player 无 Editor 依赖、默认程序集不引用可选样例、Unity meta 完整。
- C# 9 Roslyn 语法解析：Editor+Assertions 和 Player 两组宏均无语法错误。
- git diff --check：通过。

Core 新覆盖提交幂等、免费取消、资源/冷却重检、旧施放版本、付款回调结束/撤销、到期周期、不可变配置、发布过程移除、替代效果、Cue 清空重入。

### 已编写，尚未在 Unity 执行

17 个有针对性的 Unity 场景，覆盖强制校验、未知标签、参数覆盖、模块绑定、时间轴参数作用域、提交动作、无物理目标、显式物理适配和独立物理场景查询。已有测试也已改为显式物理服务与显式重新授予合同。

当前环境没有 Unity Editor，因此没有执行 Unity 导入/类型编译、EditMode、PlayMode、UI 操作验证、IL2CPP、Android 或 iOS 测量。Roslyn 语法检查不能替代这些结果。

## 明确的实现边界

- 当前 Definition / ActionDefinition 在首次 Grant 构造缓存，没有持久化 runtime-only Program。
- 当前属性聚合、Cue 时机、堆叠模型按本库合同工作，不宣称完全复制 UE 所有 GAS 行为。
- 没有网络复制、预测、回滚实现。
- 预热 Core 的 0 B 不能外推成任意 Unity 动作/整套技能系统 0 GC；目标分支、首次构造、任务和对象生成仍需目标平台剖析。
- Combat3D 是独立程序集，随同一 UPM 包交付；Realtime 编辑器的几何预演仍显式依赖它。

## 复现

```bash
python3 Tools~/verify_architecture.py
dotnet build Tests~/Core/Core.csproj -c Release -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet .build~/tests/bin/Release/net8.0/Core.dll
```

GitHub Actions 的 Verify GAS 固定 Tags 提交并执行上述门禁。Unity 验证的接入步骤见 Tests~/README.md。
