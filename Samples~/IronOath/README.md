# 铁誓当铺 / Iron Oath Pawnshop

原创手绘奇幻策略 RPG 的**首章可玩切片**。双主角、当铺经营、九宫格自动战斗和地下城寻亲使用同一套游戏状态。包含场景、界面、环境图和中文字体；无需 Spine 资源即可运行。

分支：`feat/iron-oath-pawnshop`。源分支：`feat/spine-animation-adapter`，起点 `40d081b3f5265174bcbbb373b11136377a251ac7`。

## 在 Unity 中运行

1. 使用 Unity **6000.3** 工程。安装 Unity UI（uGUI）；常规 Unity 模板一般已包含它。
2. 通过 Package Manager 的 **Add package from git URL** 安装依赖与此分支：

   ```text
   https://github.com/acced/ZFramework.GameplayTags.git#8dde1a43485f33031fd73b4e6cb21304f03c19df
   https://github.com/acced/ZFramework.GAS.git#feat/iron-oath-pawnshop
   ```

3. 在 ZFramework GAS 包的 **Samples** 中导入 **Iron Oath Pawnshop / 铁誓当铺**。
4. 选择 **Tools > Iron Oath > Open Demo**，按 **Play**。也可以直接打开导入目录中的 `Scenes/IronOathDemo.unity`。
5. 首次进入选择雷恩或克萝伊。已有存档时直接回到当铺。

导入后可先运行 **Tools > Iron Oath > Validate Imported Demo**。它检查场景入口、背景、字体、世界材质和角色绑定；修改自己的场景后使用 **Validate Current Scene**。检查不会改动场景、Prefab 或项目设置。

需要独立验收工程时，在仓库根目录执行以下命令；把版本号换成自己已安装的 Unity **6000.3** 完整版本：

```bash
python3 Tools~/prepare_iron_oath_unity.py ../IronOathAcceptance --unity-version 6000.3.0f1
```

目标目录必须是新的或为空。脚本链接当前 GAS 源码，固定 GameplayTags 提交，并复制样例与真实 Unity 测试。生成的 `ACCEPTANCE.md` 提供导入、EditMode、PlayMode 命令；修改样例后应重新生成工程。详见 [Unity 验收说明](../../Tests~/IronOath.Unity/README.md)。

界面以 **1920×1080 横屏**为基准，Canvas 居中保留完整安全区域。文字内置 `Iron Oath Serif` 中文子集，不依赖开发电脑安装中文字体。旧 Input Manager 与新 Input System 均有启动入口；使用新 Input System 的工程需已安装并启用它自己的 UI 模块。

场景只有一个显式 `IronOathDemo` 入口，导入样例不会自动接管项目其他场景。

## 第一轮建议操作

1. **当铺**：检查周报，鉴定/议价/收购，选中货架上的物品出售或翻新。魔核占 1×1，成衣护甲占 2×3。
2. **战前编排**：选择单位查看四条战术；点击切换技能、条件、目标优先级，向上移动优先条目。选择移动后点击我方格子换位。右侧前排始终面向我方。
3. **演练当前阵容**：可反复观察同一阵型的效果，不产生金币或战利品。
4. **深渊探索**：点击当前节点；战斗支持开战、暂停、1×/2×、单步、查看单位与撤退。角色下方白条是护甲、红条是生命，红豆是 AP、蓝豆是 PP。
5. **结算**：胜利后确认领取；满包时奖励整批暂存。点击当铺的“清点暂存战利品”查看金币、品相、占格与遗物标记，可以返回出售/整理库存，也可以逐件舍弃后领取保留物品。舍弃要再点一次确认；金币在最后领取时一次性结算。败退与僵持不会发放拾取物。
6. **打烊**：每天最多发起一次战斗远征，撤退也消耗次数。非战斗节点可以继续探索。周日支付房租；打烊时会自动尝试缴纳，余额不足会进入本轮经营结束界面。

首战默认战术能拿到保甲遗物和魔核；玩家可以通过修改集火目标、状态铺垫和精密刺击选择收益方式。后续三场在前三种遭遇的基础上提高 10% 生命、护甲与攻击。

## 实现内容

| 系统 | 当前行为 |
| --- | --- |
| 双主角 | 雷恩/克萝伊二选一，另一人保持合伙人与战斗伙伴身份。 |
| 战场 | 双方 3×3、每方最多 6 人，确定性速度顺序，战前换位与四条条件战术。 |
| GAS | ASC 拥有属性；真实 Ability 支付 AP/PP；共享 Effect/Calculation 计算伤害与治疗。 |
| 双伤 | 有甲时同时结算破甲与经缓冲的穿甲伤害；破甲跨越 >0→0 扣 1 PP。 |
| 控制 | 失衡→击倒、潮湿+冰/雷、油/网+火、滑倒、甲缝、精密保甲、冰封钝击 ×2.5。 |
| 空间 | 推拉、边界/占格碰撞、按厚甲与吨位计算碰撞/引雷，以及水/冰/油/火地表。 |
| 战利品 | 魔物/构装体破甲取核，人形保留 ≥80% 护甲取成衣；保住携带者装备可找回家族遗物。 |
| 当铺 | 每日有限顾客、鉴定、一次议价、收购、出售、修补、去赃印、扩建与每周行情。 |
| 日历 | 周一开局 240 金币，基础周租 320；扩建提高客流/售价与租金；周日结租。 |
| 背包 | 6×5，检查边界/碰撞，旋转和移动失败保持原物不变；可取舍超量或无法摆放的暂存战利品，保留物品整批原子领取。 |
| 首章 | 三层 15 个步进节点、六场遭遇、藏物/营地/三段线索。结局揭示父母仍活着，保留下一章悬念。 |
| 存档 | 版本化状态、深拷贝和校验；临时文件写入后替换主档，保留备份；包含战术与阵型。 |

完整伤害、状态持续与边界规则见仓库 [Development.md](../../Docs~/IronOath/Development.md)。

## 存档语义

存档写入 `Application.persistentDataPath`，文件名见 `IronOathSaveStore.FileName`。这是**遭遇开始处检查点**：游戏关闭时尚未结束的战斗，会从同一张远征票据重新开始，不额外消耗当天次数。它不保存某个攻击动画的中间帧。已经进入暂存的奖励连同票据保存，重复结算不会多发钱或物品。

UI 会在交易、编队/战术修改、战利品取舍、日历推进、远征开始和结算等状态变化后保存；手动“保存”按钮会报告写入是否成功。损坏主档会尝试读取备份，并提示恢复来源。恢复后的首次保存保留有效备份，不会把损坏主档轮换进备份。

`IronOathSaveStore.FileName` 保持原来的 `iron-oath-campaign-v1.json`，合法 v1 存档继续可读。经营状态、六人站位和完整战术一起校验；重复站位、缺行/重复战术、角色不能使用的启用技能或未知版本会拒绝整份档案并尝试备份。舍弃的遗物不授予线索；即使舍弃全部暂存物品，也仍需点击领取，才会到账金币并推进远征节点。

## 接入自己的 Spine 角色

先运行默认占位场景，再替换自己的角色：

- 在场景的 `IronOathDemo` Inspector 中展开 **Actors**；`unitDefinitionId` 对应 `rayn`、`chloe`、`brann`、`mira`、`sable`、`wren` 或敌方定义 ID。留空可作为兜底 Prefab。
- 拖入世界空间角色 Prefab，根节点放在脚底，设置 `worldScale`、`footOffset`、原图朝向。场景会自动镜像敌方、将前排朝中线摆放并设置行遮挡。
- 克隆实例保留 Prefab 原有缩放、旋转和子 Renderer 的排序；独立外层负责校准与镜像，克隆根节点的位移会归零。角色的 `SortingGroup` 统一控制行遮挡；嵌套排序组留在角色组内。
- `Battle Layer` 默认使用第 30 层，可在 Inspector 中改为项目的空闲层。舞台相机只渲染此层，实例子对象统一分配到此层；现有项目中的其他相机也应排除此层。样例不修改 `TagManager` 或其他相机。
- 角色下方 HUD 是独立层，角色攻击位移与前后遮挡不会把血条、名字和豆子盖住。
- 如果需要把已结算事件播放为 Spine 动画，按照 [OptionalSpine~](OptionalSpine~/README.md) 的步骤安装现有适配包并复制可选绑定脚本。

这里只提供位置、朝向、排序和事件接入，不生成或附带任何 Spine 骨骼。默认角色为世界空间 MeshRenderer 纹章占位体，与实际角色共用世界脚底锚点和排序；主角选择卡上的纹章图仍为 UI 装饰。站位标记、阴影与背景也在世界空间，UGUI 根据世界坐标投影显示血条、交互区域与反馈。默认世界材质不依赖灯光；自己的角色仍使用其原有材质。

## 文件结构

```text
Runtime/Domain/Combat       纯 C# GAS 战斗规则
Runtime/Domain/Campaign     经营、格子背包、探索与存档
Runtime/Domain/Bridge       实际战斗结果到经营奖励
Runtime/Presentation        UGUI、战斗舞台、存档与角色表现接口
Editor                      打开样例与只读导入/角色绑定检查菜单
Scenes                      可直接打开的场景
Resources/IronOath/Art       三张原创环境画
Resources/IronOath/Fonts     中文字体子集与 OFL 许可证
Resources/IronOath/WorldUnlit.shader  世界占位体、标记与背景共用材质
OptionalSpine~              用户选择安装后才启用的 Spine 表现绑定
```

## 验证范围与制作边界

真实 GAS/.NET 回归验证玩法数值、控制、背包、房租、确定性、奖励防重和全章循环；47 组游戏回归中包含 6 组真实文件读写、主备档恢复和完整配置校验。可移植存档测试注入 System.Text.Json，真实 JsonUtility、场景导入、UI 点击和世界舞台测试另在 `Tests~/IronOath.Unity` 内。

开发环境没有 Unity Editor，**尚未执行真实 Unity 导入、EditMode/PlayMode、实际画面验收或 IL2CPP/手机构建**。临时 Unity 类型桩的编译只检查样例内部接口与语言兼容性，不代表 Unity 编译通过。

这是一章可扩展的游戏切片。最终角色美术/Spine 动画、音效配音、全量剧情、多章节内容与目标设备性能打磨仍需继续制作。环境美术和界面方向已经落实，不将占位角色包装成 AAA 成品。

美术来源与完整生成提示词见 [ArtDirection.md](ArtDirection.md)。
