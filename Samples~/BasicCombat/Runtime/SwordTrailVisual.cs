using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat
{
    /// <summary>
    /// 剑光拖尾：由剑身两端的轨迹拼成条带，越旧越淡。
    /// 编辑器预览使用生成时烘焙的轨迹（按动画时间回放）；运行期改为实时记录剑身位置，攻速变化时同样贴合。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class SwordTrailVisual : CueVisual
    {
        [SerializeField] private string bladeBasePath, bladeTipPath;
        private Transform _owner, bladeBase, bladeTip;
        [SerializeField] private Color color = Color.white;
        [Tooltip("拖尾保留的时长（秒）")]
        [SerializeField] private float length = .14f;
        [SerializeField] private float bakedRate = 60;
        [Tooltip("烘焙轨迹，相对角色根节点；第 0 个样本对应 Cue 区间开始")]
        [SerializeField] private Vector3[] bakedBase = new Vector3[0], bakedTip = new Vector3[0];

        private readonly List<(float time, Vector3 a, Vector3 b)> _live = new();
        private Mesh _mesh;
        private float _liveClock;

        public void Configure(string basePath, string tipPath, Color tint, float trailLength, float rate, Vector3[] baseSamples, Vector3[] tipSamples)
        {
            bladeBasePath = basePath; bladeTipPath = tipPath; color = tint; length = trailLength; bakedRate = rate; bakedBase = baseSamples; bakedTip = tipSamples;
        }

        public override void Initialize(Transform owner)
        {
            _owner = owner; bladeBase = owner.Find(bladeBasePath); bladeTip = owner.Find(bladeTipPath);
        }
        public override void OnCueStart() => Begin();
        public override void Sample(float elapsed, float deltaTime, bool preview)
        { if (preview) Evaluate(elapsed); else Record(deltaTime); }

        /// <summary>编辑器预览：按烘焙轨迹重建 elapsed 之前 <see cref="length"/> 秒的条带。</summary>
        public override void Evaluate(float elapsed)
        {
            var points = new List<(float age, Vector3 a, Vector3 b)>();
            if (bakedBase.Length > 1)
            {
                float start = Mathf.Max(0, elapsed - length);
                for (float t = start; ; t += 1f / bakedRate)
                {
                    bool last = t >= elapsed;
                    float sample = Mathf.Min(t, elapsed) * bakedRate;
                    int i = Mathf.Clamp(Mathf.FloorToInt(sample), 0, bakedBase.Length - 2);
                    float f = Mathf.Clamp01(sample - i);
                    Vector3 Local(Vector3 point) => _owner != null ? transform.InverseTransformPoint(_owner.TransformPoint(point)) : point;
                    // 历史轨迹来自烘焙；最新一对顶点贴住当前采样姿势，避免高速挥剑时插值误差造成脱节。
                    points.Add(last && bladeBase != null && bladeTip != null
                        ? (0, transform.InverseTransformPoint(bladeBase.position), transform.InverseTransformPoint(bladeTip.position))
                        : (elapsed - Mathf.Min(t, elapsed), Local(Vector3.Lerp(bakedBase[i], bakedBase[i + 1], f)), Local(Vector3.Lerp(bakedTip[i], bakedTip[i + 1], f))));
                    if (last) break;
                }
            }
            // 烘焙数据在角色根空间，转换到独立特效实例的本地空间。
            Build(points);
        }

        /// <summary>运行期：区间开始时清空轨迹。</summary>
        public void Begin() { _live.Clear(); _liveClock = 0; }

        /// <summary>运行期：记录当前剑身位置并重建条带。</summary>
        public void Record(float deltaTime)
        {
            if (bladeBase == null || bladeTip == null) return;
            _liveClock += deltaTime;
            _live.Add((_liveClock, transform.InverseTransformPoint(bladeBase.position), transform.InverseTransformPoint(bladeTip.position)));
            while (_live.Count > 2 && _liveClock - _live[0].time > length) _live.RemoveAt(0);
            var points = new List<(float age, Vector3 a, Vector3 b)>(_live.Count);
            for (int i = 0; i < _live.Count; i++) points.Add((_liveClock - _live[i].time, _live[i].a, _live[i].b));
            Build(points);
        }

        private void Build(List<(float age, Vector3 a, Vector3 b)> points)
        {
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "SwordTrail", hideFlags = HideFlags.DontSave };
                _mesh.MarkDynamic();
                GetComponent<MeshFilter>().sharedMesh = _mesh;
            }
            _mesh.Clear();
            if (points.Count < 2) return;
            // 用 Catmull-Rom 加密，挥砍弧线才圆润。
            var dense = new List<(float age, Vector3 a, Vector3 b)>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p0 = points[Mathf.Max(0, i - 1)]; var p1 = points[i]; var p2 = points[i + 1]; var p3 = points[Mathf.Min(points.Count - 1, i + 2)];
                for (int s = 0; s < 4; s++)
                {
                    float u = s / 4f;
                    dense.Add((Mathf.Lerp(p1.age, p2.age, u), CatmullRom(p0.a, p1.a, p2.a, p3.a, u), CatmullRom(p0.b, p1.b, p2.b, p3.b, u)));
                }
            }
            dense.Add(points[points.Count - 1]);
            var vertices = new Vector3[dense.Count * 2];
            var uvs = new Vector2[dense.Count * 2];
            var colors = new Color[dense.Count * 2];
            var triangles = new int[(dense.Count - 1) * 12];
            for (int i = 0; i < dense.Count; i++)
            {
                float fade = 1 - Mathf.Clamp01(dense[i].age / length);
                vertices[i * 2] = dense[i].a; vertices[i * 2 + 1] = dense[i].b;
                uvs[i * 2] = new Vector2(fade, 0); uvs[i * 2 + 1] = new Vector2(fade, 1);
                var c = new Color(1, 1, 1, fade * fade);
                colors[i * 2] = c; colors[i * 2 + 1] = c;
            }
            for (int i = 0, t = 0; i < dense.Count - 1; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                // 双面：挥砍方向随视角变化，两面都要可见。
                triangles[t++] = a; triangles[t++] = b; triangles[t++] = d; triangles[t++] = a; triangles[t++] = d; triangles[t++] = c;
                triangles[t++] = a; triangles[t++] = d; triangles[t++] = b; triangles[t++] = a; triangles[t++] = c; triangles[t++] = d;
            }
            _mesh.vertices = vertices; _mesh.uv = uvs; _mesh.colors = colors; _mesh.triangles = triangles;
            _mesh.RecalculateBounds();
            Tint(GetComponent<MeshRenderer>(), color);
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return .5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        private void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
        }
    }
}
