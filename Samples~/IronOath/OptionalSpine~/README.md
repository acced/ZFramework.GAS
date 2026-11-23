# 可选 Spine 表现绑定

默认样例不依赖 Spine SDK；本目录末尾的 `~` 使 Unity 忽略它。

1. 按仓库 `Integrations~/Spine/README.md` 安装官方 Spine 4.3 和现有适配包；GAS 主包和适配包 URL 都固定到 `feat/iron-oath-pawnshop`。
2. 将本目录复制到项目 `Assets/IronOathSpine`（新目录名不带 `~`）。
3. 在你自己的世界空间 `SkeletonAnimation` Prefab 添加 `IronOathSpinePresentation`，配置技能、受击、破甲和倒下的官方 `AnimationReferenceAsset`。不需要全部配置，未配置的事件直接忽略。
4. 在 `IronOathDemo` Inspector 的 **Actors** 添加单位 ID 与 Prefab。根节点是脚底原点，默认源图朝右；可用 `worldScale` 和 `footOffset` 校准。基础待机由 Prefab 的轨道 0 负责，适配器独占轨道 1。

左右阵营、镜像、九宫格脚底锚点、行遮挡层级、血条挂点由 `BattleStage` 管理。动画接收已经结算的战斗事件，仅产生表现；不得在 Spine Event / Complete 回调中再次伤害或扣 AP。这个离散战斗样例没有等待动画命中帧后才结算的第二条玩法时钟。

脚本执行顺序为 -200，使技能采样位置在 Spine 常规更新前写入。暂停停止新的战斗行动，已触发的短表现允许完成。没有生成、附带或假设任何 Spine 骨骼资源。

真实 Spine Prefab 导入、动画映射与渲染需在 Unity 6000.3 工程验收。
