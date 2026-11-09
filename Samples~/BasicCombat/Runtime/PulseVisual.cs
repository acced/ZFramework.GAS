using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat
{
    /// <summary>缩放、透明度与自转都随时间变化的发光件：光环、法阵、蓄能光球。</summary>
    public sealed class PulseVisual : CueVisual
    {
        [SerializeField] private Renderer[] renderers = new Renderer[0];
        [SerializeField] private Color color = Color.white;
        [SerializeField] private AnimationCurve scale = AnimationCurve.Constant(0, 1, 1);
        [SerializeField] private AnimationCurve alpha = AnimationCurve.Constant(0, 1, 1);
        [Tooltip("绕本地 Y 轴的自转速度（度/秒）")]
        [SerializeField] private float spin;
        [Tooltip("大于 0 时叠加该周期的明暗呼吸")]
        [SerializeField] private float pulsePeriod;

        public void Configure(Renderer[] targets, Color tint, AnimationCurve scaleCurve, AnimationCurve alphaCurve, float spinSpeed, float pulse)
        {
            renderers = targets; color = tint; scale = scaleCurve; alpha = alphaCurve; spin = spinSpeed; pulsePeriod = pulse;
        }

        public override void Evaluate(float elapsed)
        {
            transform.localScale = Vector3.one * scale.Evaluate(elapsed);
            transform.localRotation = Quaternion.Euler(0, elapsed * spin, 0);
            float a = alpha.Evaluate(elapsed);
            if (pulsePeriod > 0) a *= .78f + .22f * Mathf.Cos(elapsed / pulsePeriod * Mathf.PI * 2);
            var tint = new Color(color.r, color.g, color.b, color.a * Mathf.Clamp01(a));
            foreach (var r in renderers) if (r != null) Tint(r, tint);
        }
    }
}
