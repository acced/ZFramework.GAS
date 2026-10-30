using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat
{
    /// <summary>
    /// 一段 Cue 区间的表现。只依赖「区间开始后经过了多久」，
    /// 因此运行期由 Cue 处理器每帧推进，编辑器预览按时间轴位置直接求值，两者画面一致。
    /// </summary>
    public abstract class CueVisual : CueVfxBehaviour
    {
        public override void Sample(float elapsed, float deltaTime, bool preview) => Evaluate(elapsed);

        /// <param name="elapsed">区间开始后的秒数（授权时间）。</param>
        public abstract void Evaluate(float elapsed);

        private static MaterialPropertyBlock _block;
        protected static void Tint(Renderer renderer, Color color)
        {
            _block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            _block.SetColor("_TintColor", color);
            renderer.SetPropertyBlock(_block);
        }
    }
}
