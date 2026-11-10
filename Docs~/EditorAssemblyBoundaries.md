# 程序集边界

| 程序集 | 引用本包程序集 | 责任 |
| --- | --- | --- |
| ZFramework.GAS | 无 | 不引用 Unity 的运行时机制 |
| ZFramework.GAS.Unity | GAS | ScriptableObject 资产、动作、Runner、服务与查询协议 |
| ZFramework.GAS.Unity.Combat3D | GAS、Unity | 物理查询与碰撞体槽位 |
| ZFramework.GAS.Unity.Realtime | GAS、Unity | 时间轴、Playables、Cue/VFX 与音效 |
| ZFramework.GAS.Editor | GAS、Unity | 主编辑器、运行时调试、校验 |
| ZFramework.GAS.Editor.Realtime | GAS、Unity、Realtime、Combat3D、Editor | 时间轴与预览接入 |
| ZFramework.GAS.Unity.Spine（可选包） | Realtime | Spine 4.3 技能轨道驱动；另引用 spine-csharp、spine-unity |
| ZFramework.GAS.Editor.Spine（可选包） | Editor.Realtime | AnimationReferenceAsset 与 Spine 姿势预览；另引用 spine-csharp、spine-unity |

Core 声明 noEngineReferences。基础 Unity 不直接使用物理 API；基础 Editor 不引用 Realtime。Realtime 编辑器用明确的扩展协议接入主窗口。Player 程序集不反向引用 Editor。

Samples~ 不自动导入；生产程序集不能依赖样例或测试程序集。Spine 集成位于 Integrations~/Spine，通过独立 package.json 和 Git 子路径按需安装。主包导入时忽略外层 Integrations~；单独安装后，适配包内部 Runtime/Editor 使用正常程序集与 metadata。依赖只能由适配包指向主包，主包没有 Spine 类型或程序集引用。

```bash
python3 Tools~/verify_architecture.py
```

脚本检查源码程序集归属、依赖环、基础层引用、物理边界、Unity metadata 和样例声明。可选子包按自己的根目录校验 metadata 与测试边界，并检查跨包程序集依赖已经在包清单声明。它不进行 Unity 类型检查；实际编译和编辑器行为需要 Unity 验证。
