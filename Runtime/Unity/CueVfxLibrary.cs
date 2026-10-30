using System;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public enum CueVfxStopMode
    {
        [InspectorName("立即移除")] Immediate,
        [InspectorName("停止发射，保留尾迹")] StopEmitting
    }

    [Serializable]
    public sealed class CueVfxBinding
    {
        [InspectorName("Cue 标签")] public GameplayTag cueTag;
        [InspectorName("特效 Prefab")] public GameObject prefab;
        [InspectorName("挂点路径"), Tooltip("相对 CueVfxPlayer 的 Transform 路径，空表示角色根节点；例如 Rig/Hand/SwordTip。")]
        public string anchorPath = "";
        [InspectorName("跟随挂点")] public bool follow = true;
        [InspectorName("位置偏移")] public Vector3 position;
        [InspectorName("旋转偏移")] public Vector3 rotation;
        [InspectorName("停止方式")] public CueVfxStopMode stopMode;
        [InspectorName("尾迹最长时间"), Min(.01f), Tooltip("停止发射后等待粒子消失，最多保留此秒数。无粒子的自定义特效也按此时间回收。")]
        public float tailSeconds = 2;

        public Transform FindAnchor(Transform root) => string.IsNullOrEmpty(anchorPath) ? root : root.Find(anchorPath);
    }

    /// <summary>角色运行时和技能编辑器预览共用的 Cue 到特效映射。同一标签允许配置多个特效。</summary>
    [CreateAssetMenu(menuName = "ZFramework GAS/Cue VFX Library", fileName = "CueVfxLibrary")]
    public sealed class CueVfxLibrary : ScriptableObject
    {
        [InspectorName("特效映射")] public CueVfxBinding[] bindings = Array.Empty<CueVfxBinding>();
    }
}
