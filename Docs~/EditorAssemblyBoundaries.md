# 程序集边界

| 程序集 | 引用本包程序集 | 责任 |
| --- | --- | --- |
| ZFramework.GAS | 无 | 不引用 Unity 的运行时机制 |
| ZFramework.GAS.Unity | GAS | ScriptableObject 资产、动作、Runner、服务与查询协议 |
| ZFramework.GAS.Unity.Combat3D | GAS、Unity | 物理查询与碰撞体槽位 |
| ZFramework.GAS.Unity.Realtime | GAS、Unity | 时间轴、Playables、Cue/VFX 与音效 |
| ZFramework.GAS.Editor | GAS、Unity | 主编辑器、运行时调试、校验 |
| ZFramework.GAS.Editor.Realtime | GAS、Unity、Realtime、Combat3D、Editor | 时间轴与预览接入 |

Core 声明 noEngineReferences。基础 Unity 不直接使用物理 API；基础 Editor 不引用 Realtime。Realtime 编辑器用明确的扩展协议接入主窗口。Player 程序集不反向引用 Editor。

Samples~ 不自动导入；生产程序集不能依赖样例或 Tests~。Spine runtime/editor 及其专用程序集已删除。

```bash
python3 Tools~/verify_architecture.py
```

脚本检查源码程序集归属、依赖环、基础层引用、物理边界、Unity metadata 和样例声明。它不进行 Unity 类型检查；实际编译和编辑器行为需要 Unity 验证。
