### 通过技能资产统一施放

每个角色持有一个 `SkillRunner`，复用该角色的 ASC 与动画驱动。训练场、AI 或输入层只传入技能资产：

```csharp
var runner = new SkillRunner(owner, () => skillAnimator.Driver);
runner.Grant(abilityAsset); // 可预先授予；TryCast 首次使用也会自动授予
runner.MarkerReached += (asset, marker) => HandleSkillEvent(asset, marker);
var result = runner.TryCast(abilityAsset, targetAsc, taskContext);
```

`Grant` 按资产去重，各角色拥有独立 Spec。`TryCast` 返回 Core 的激活结果；成功后内部创建 `AnimationTask`。无动画技能立即结束，非循环动画结束时结束技能，循环技能由 `Cancel(asset)` 或 ASC 的取消逻辑终止。`Revoke(asset)` 撤销授予。HUD 使用 `IsActive(asset)`、`CooldownRemaining(asset)` 和 `CooldownDuration(asset)` 查询状态。

宿主仍负责推进 `owner.Tick(dt)`，提供目标及 `TimelineTaskContext`，实现召唤等业务回调。当前入口采用“动画结束即技能结束”的生命周期策略；需要后摇或其他完成条件时仍可直接使用底层 `AnimationTask`。运行期状态不写回共享资产。驱动启动异常会结束激活状态，已提交的消耗和冷却不自动退款。

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

拖动过程只更新视觉位置，抬手一次写回并形成一步 Undo。时间轴元素的稳定 ID 由编辑器自动维护，用于保持数组重排后的端口和选择对应关系。

预览使用隔离的 PreviewRenderUtility 场景，不驱动战斗规则。拖动旋转、滚轮缩放、双击聚焦。预览角色上的 `CueVfxPlayer` 引用 `CueVfxLibrary`，运行时与编辑器读取同一份 Cue → 特效 Prefab／挂点／跟随／停止方式映射。普通粒子无需自定义脚本；自定义网格可继承 `CueVfxBehaviour`。`ICuePreview` 仍可用于其他项目扩展。顶部后端菜单切换预览角色上已有 SkillAnimator 的策略。

通过 Create → ZFramework GAS → Cue VFX Library 创建映射资产，再赋给角色的 Cue VFX Player。挂点为相对该组件的 Transform 路径，留空表示根节点；不跟随时保留区间开始那一刻的挂点世界变换（不自动贴地或射线投射）。运行时在创建 ASC 后调用 `player.Bind(asc)`，组件自动注册 Cue 并在 LateUpdate 推进表现；禁用、销毁和重新绑定会清理所有实例与监听。映射使用精确标签匹配，只处理 OnActive／Removed 区间事件。相同标签重叠区间共用一组实例，计数归零时停止。

立即移除会在区间结束／技能打断时清理；停止发射模式保留剩余粒子，直到粒子死亡或超过尾迹最长时间。预览可任意前后拖动，粒子使用固定随机种子从零模拟，并展示停止后的尾迹。固定在世界的特效从区间开始姿势定位。内置支持 ParticleSystem 和显式采样的 CueVfxBehaviour；自定义 MonoBehaviour、VFX Graph、音频与特效内部 Animator 不会自动获得时间轴预览。

### 校验与示例

消息页提供 GAS001–004、010–013、020–022、030–032 检查、定位和适用的自动修复。需用户选择的配置仍在 Details 中处理，不会猜测标签或资产。菜单 `Tools → ZFramework GAS → 校验所有技能` 可批量检查；CI 可调用 `ZFramework.GAS.Editor.BatchValidate.Run`，存在错误时退出码非零。批量校验不会为资产补 ID。

`Tools → ZFramework GAS → 创建 BasicCombat 示例` 一次生成五种技能、属性词表、骑士预制体、Animator 动作、独立特效 Prefab 及 Cue 特效映射库，并自动接好角色和技能预览，全部位于 `Assets/GAS Samples/BasicCombat`。斩击包含刀光 Cue、霸体和连招窗口；战吼包含独立地面粒子法阵。已有示例目录会在确认后重新生成；已打开的示例场景会重新载入生成结果。标签注册与宿主接入见 `Samples/BasicCombat/README.md`。

