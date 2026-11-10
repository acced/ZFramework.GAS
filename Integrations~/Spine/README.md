# ZFramework GAS Spine

可选的 Spine 4.3 动画驱动与技能编辑器姿势预览。与 GAS 同仓库维护，作为独立 UPM 包安装。只安装 GAS 主包时，`Integrations~` 不参与 Unity 导入和编译。

## 要求与安装

- Unity 6000.3，GAS 0.3.0。
- 官方 UPM：`spine-csharp` 4.3.42、`spine-unity` 4.3.119。
- 本次适配依据官方源码提交 `175a1e05073a406f305bde2621c065b8c2256c42`；只针对 Spine 4.3，不包含 SDK 或演示资源。

Git 依赖必须在使用项目的 `Packages/manifest.json` 中声明来源，包自身的 `package.json` 只声明包名和版本。以下是当前开发分支的安装示例，合并后可将两个 GAS URL 固定到同一个发布标签或提交：

```json
{
  "dependencies": {
    "com.acced.zframework.gameplaytags": "https://github.com/acced/ZFramework.GameplayTags.git#8dde1a43485f33031fd73b4e6cb21304f03c19df",
    "com.acced.zframework.gas": "https://github.com/acced/ZFramework.GAS.git#feat/spine-animation-adapter",
    "com.esotericsoftware.spine.spine-csharp": "https://github.com/EsotericSoftware/spine-runtimes.git?path=spine-csharp/src#175a1e05073a406f305bde2621c065b8c2256c42",
    "com.esotericsoftware.spine.spine-unity": "https://github.com/EsotericSoftware/spine-runtimes.git?path=spine-unity/Assets/Spine#175a1e05073a406f305bde2621c065b8c2256c42",
    "com.acced.zframework.gas.spine": "https://github.com/acced/ZFramework.GAS.git?path=/Integrations~/Spine#feat/spine-animation-adapter"
  }
}
```

将这些条目合并到现有 manifest，保留项目其他依赖。本包声明的是官方 UPM 依赖；已经使用 `.unitypackage` 安装 SDK 的项目，应先按官方流程统一安装方式，避免同名程序集重复导入。

## Runtime

角色使用世界空间 `SkeletonAnimation` 和 `SkeletonRenderer`。角色创建时绑定一次，后续仍通过 `SkillRunner.TryCast` 施放：

```csharp
using Spine.Unity;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Unity.Spine;

// skeletonAnimation 的 SkeletonData 已由角色配置。
var presentation = new SkillTimelinePlayer(
    runner, new SpineAnimationDriver(skeletonAnimation, trackIndex: 1));

runner.Grant(skill);
runner.TryCast(skill, target);

// 在项目的 Update / 模拟循环中，先推进 GAS，再让 Spine 正常更新。
owner.Tick(deltaTime);

// 角色退出时结束所属工作，再释放表现绑定。
owner.Clear();
presentation.Dispose();
```

技能的 `SkillAnimationAsset.clip` 直接引用官方 `AnimationReferenceAsset`。公共资产只有一个动画资源字段，没有 Spine 专属技能类型、片段包装资产或第二个施放入口。

### 时间与所有权

- 时间轴的 `Duration` 决定业务节奏；采样位置为 `normalizedTime × Spine 动画原始时长`。
- 驱动独占构造时指定的技能轨道，默认 1。项目的基础动画可使用轨道 0；其他代码不要同时改写技能轨道。
- 技能 entry 的 `TimeScale` 为 0，由 GAS 定位。Spine 宿主正常推进基础动画和混合；不要把整个 `SkeletonAnimation` 或 `AnimationState` 冻结。
- `Advance` 不调用 `Update` 或 `Apply`。同帧跨过多个循环也不会多推进混合或重复派发原生动画事件。项目应安排 GAS Tick 先于 Spine 的常规更新。
- `Stop` 将技能轨道混出到下层姿势，宿主在任务结束后继续完成混出；`Dispose` 清理技能轨道并退订驱动的回调。
- Marker、Cue、Tag 窗口与取消仍由 GAS 时间轴处理，不把 Spine `Event` / `Complete` / `End` 映射成业务结算。

Spine 的动画切换会同步调用订阅者。驱动在 Start 通知中配置当前 entry，保留播放版本与最新采样位置，防止取消或重施放后继续写入旧 entry。混出使用一个共享的无曲线动画数据，避免在原生事件派发结束后再配置可能已经失效的 entry。它不是 SDK 的内部空动画单例，因此外部代码不能依赖此 entry 的 `IsEmptyAnimation` 标记。

当前公共时间轴保留原有边界处理顺序，不保证 Marker 执行时骨骼已经同步到该边界时间。精确骨骼命中需要额外定义姿势采样、挂点和碰撞同步顺序；本适配不提供 Spine Bounding Box 或 Physics2D 战斗规则。

## 编辑器

1. 使用 Spine 官方 `SkeletonDataAsset` Inspector 生成 `AnimationReferenceAsset`。
2. 在现有技能编辑器的动画片段字段中选择该引用；“同步时长”读取 Spine 动画长度，沿用现有时间轴重映射和 Undo。
3. 给技能设置使用相同 `SkeletonDataAsset` 的预览 Prefab。
4. 在同一时间轴编辑 Marker、Cue、Tag 窗口及现有轨道，拖动播放头查看姿势。

资源适配只在 Editor 注册。F7 / 构建检查支持的资源类型、引用解析和源动画时长；SkeletonData 与预览角色不匹配时在预览中报告问题，不替换角色数据。

预览仅操作隔离副本，禁用其自动行为，每次按绝对时间恢复 setup pose、应用曲线并更新网格。反向拖拽不运行真实技能或 AnimationState 事件；物理约束每次重置，不重演历史模拟。此版本面向 `SkeletonAnimation`，不包含 `SkeletonGraphic`、`SkeletonMecanim`、皮肤管理或基础移动状态机。

## 验证

在 Unity 测试工程安装上述包，并在项目 manifest 根对象中加入：

```json
"testables": ["com.acced.zframework.gas.spine"]
```

`Tests/Editor` 的测试程序集只在启用包测试时使用。它覆盖原生 Spine 混入混出、归一化采样、同步回调取消/重施放、释放隔离，以及预览逆向采样、恢复 setup pose 和资源不匹配。

本次开发环境没有 Unity Editor，未执行 Unity 语义编译、EditMode、PlayMode、实际网格显示或 IL2CPP。仓库的 Core / 架构 CI 不替代这些验证。

## 参考

- [官方 Spine 4.3 UPM 安装](https://esotericsoftware.com/spine-unity-download)
- [AnimationReferenceAsset](https://esotericsoftware.com/spine-unity-assets)
- [AnimationState 和手动更新](https://esotericsoftware.com/spine-unity-main-components)
- [本次核对的 Spine 源码](https://github.com/EsotericSoftware/spine-runtimes/tree/175a1e05073a406f305bde2621c065b8c2256c42)
- [Unity Git 包和子目录语法](https://docs.unity3d.com/6000.0/Documentation/Manual/upm-git.html)
- [Unity 包布局与忽略目录](https://docs.unity3d.com/6000.0/Documentation/Manual/cus-layout.html)

本包桥接代码使用 MIT 许可证，见 LICENSE。Spine SDK 及其资产遵循官方许可。
