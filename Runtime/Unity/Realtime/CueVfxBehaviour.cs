using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>可选的自定义特效扩展。普通 ParticleSystem Prefab 无需添加此组件。</summary>
    public abstract class CueVfxBehaviour : MonoBehaviour
    {
        public virtual void Initialize(Transform owner) { }
        public virtual void OnCueStart() { }
        public abstract void Sample(float elapsed, float deltaTime, bool preview);
        public virtual void OnCueStop() { }
    }
}
