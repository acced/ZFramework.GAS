using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>
    /// 示例美术用到的程序化网格。全部由代码生成，示例不依赖任何第三方资源。
    /// 法线一律解析计算或按面分离，避免接缝与非等比缩放带来的明暗错误。
    /// </summary>
    internal static class MeshKit
    {
        /// <summary>
        /// 绕 Y 轴的旋转体。<paramref name="profile"/> 为自下而上的 (半径, 高度)；
        /// 相邻两点重合表示硬边。截面按 <paramref name="scaleX"/>/<paramref name="scaleZ"/> 拉成椭圆。
        /// </summary>
        public static Mesh Lathe(string name, IList<Vector2> profile, int segments = 28, float scaleX = 1, float scaleZ = 1,
            bool capBottom = true, bool capTop = true, float zOffsetPerY = 0)
        {
            var b = new Builder(name);
            int n = profile.Count;
            for (int i = 0; i < n; i++)
            {
                // 重合点两侧各取单侧切线，形成硬边。
                bool sharpBefore = i > 0 && profile[i] == profile[i - 1];
                bool sharpAfter = i < n - 1 && profile[i] == profile[i + 1];
                Vector2 prev = i > 0 && !sharpBefore ? profile[i - 1] : profile[i];
                Vector2 next = i < n - 1 && !sharpAfter ? profile[i + 1] : profile[i];
                if (prev == next) { prev = i > 0 ? profile[i - 1] : profile[i]; next = i < n - 1 ? profile[i + 1] : profile[i]; }
                Vector2 tangent = (next - prev).normalized;
                var outward = new Vector2(tangent.y, -tangent.x);
                for (int s = 0; s <= segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    float c = Mathf.Cos(a), sn = Mathf.Sin(a);
                    var p = new Vector3(c * profile[i].x * scaleX, profile[i].y, sn * profile[i].x * scaleZ + profile[i].y * zOffsetPerY);
                    var normal = new Vector3(outward.x * c / Mathf.Max(.001f, scaleX), outward.y, outward.x * sn / Mathf.Max(.001f, scaleZ)).normalized;
                    b.Vertex(p, normal, new Vector2(s / (float)segments, i / (float)(n - 1)));
                }
            }
            int ring = segments + 1;
            for (int i = 0; i < n - 1; i++)
            {
                if (profile[i] == profile[i + 1]) continue;
                for (int s = 0; s < segments; s++)
                {
                    int a = i * ring + s, c = a + ring;
                    b.Quad(a, c, c + 1, a + 1);
                }
            }
            if (capBottom && profile[0].x > .0001f) Cap(b, profile[0], segments, scaleX, scaleZ, zOffsetPerY, false);
            if (capTop && profile[n - 1].x > .0001f) Cap(b, profile[n - 1], segments, scaleX, scaleZ, zOffsetPerY, true);
            return b.Build();
        }

        private static void Cap(Builder b, Vector2 at, int segments, float sx, float sz, float zOffsetPerY, bool top)
        {
            var normal = top ? Vector3.up : Vector3.down;
            int center = b.Vertex(new Vector3(0, at.y, at.y * zOffsetPerY), normal, new Vector2(.5f, .5f));
            for (int s = 0; s <= segments; s++)
            {
                float a = s / (float)segments * Mathf.PI * 2f;
                b.Vertex(new Vector3(Mathf.Cos(a) * at.x * sx, at.y, Mathf.Sin(a) * at.x * sz + at.y * zOffsetPerY), normal,
                    new Vector2(.5f + Mathf.Cos(a) * .5f, .5f + Mathf.Sin(a) * .5f));
            }
            for (int s = 0; s < segments; s++)
            {
                if (top) b.Triangle(center, center + s + 2, center + s + 1);
                else b.Triangle(center, center + s + 1, center + s + 2);
            }
        }

        /// <summary>圆角盒。圆角处按角度采样，平面部分只用一段，保证轮廓圆润而面数可控。</summary>
        public static Mesh RoundedBox(string name, Vector3 size, float radius, int roundSteps = 5)
        {
            var b = new Builder(name);
            Vector3 half = size * .5f;
            radius = Mathf.Min(radius, Mathf.Min(half.x, Mathf.Min(half.y, half.z)) * .999f);
            Vector3 inner = half - Vector3.one * radius;
            float[] Axis(float h, float i)
            {
                var list = new List<float>();
                for (int k = 0; k <= roundSteps; k++) list.Add(-i - radius * Mathf.Cos(k / (float)roundSteps * Mathf.PI * .5f));
                for (int k = roundSteps; k >= 0; k--) list.Add(i + radius * Mathf.Cos(k / (float)roundSteps * Mathf.PI * .5f));
                return list.ToArray();
            }
            float[] ax = Axis(half.x, inner.x), ay = Axis(half.y, inner.y), az = Axis(half.z, inner.z);
            void Face(int normalAxis, float sign)
            {
                int ua = (normalAxis + 1) % 3, va = (normalAxis + 2) % 3;
                float[] us = ua == 0 ? ax : ua == 1 ? ay : az, vs = va == 0 ? ax : va == 1 ? ay : az;
                int start = b.Count;
                for (int j = 0; j < vs.Length; j++)
                    for (int i = 0; i < us.Length; i++)
                    {
                        var q = Vector3.zero; q[normalAxis] = sign * half[normalAxis]; q[ua] = us[i]; q[va] = vs[j];
                        var c = new Vector3(Mathf.Clamp(q.x, -inner.x, inner.x), Mathf.Clamp(q.y, -inner.y, inner.y), Mathf.Clamp(q.z, -inner.z, inner.z));
                        Vector3 d = q - c; var n = d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.zero;
                        if (n == Vector3.zero) n[normalAxis] = sign;
                        b.Vertex(c + n * radius, n, new Vector2(i / (float)(us.Length - 1), j / (float)(vs.Length - 1)));
                    }
                int w = us.Length;
                for (int j = 0; j < vs.Length - 1; j++)
                    for (int i = 0; i < w - 1; i++)
                    {
                        int a = start + j * w + i;
                        if (sign > 0) b.Quad(a, a + 1, a + w + 1, a + w); else b.Quad(a, a + w, a + w + 1, a + 1);
                    }
            }
            for (int axis = 0; axis < 3; axis++) { Face(axis, 1); Face(axis, -1); }
            return b.Build();
        }

        /// <summary>沿路径扫掠的圆管。标架按平行移动推进，弯曲处不会扭转。</summary>
        public static Mesh Sweep(string name, IList<Vector3> path, IList<float> radii, int segments = 12, bool caps = true, float flatten = 1)
        {
            var b = new Builder(name);
            int n = path.Count;
            Vector3 tangent = (path[1] - path[0]).normalized;
            Vector3 normal = Vector3.Cross(tangent, Mathf.Abs(Vector3.Dot(tangent, Vector3.up)) > .9f ? Vector3.right : Vector3.up).normalized;
            var frames = new (Vector3 t, Vector3 n)[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 t = i == 0 ? path[1] - path[0] : i == n - 1 ? path[n - 1] - path[n - 2] : path[i + 1] - path[i - 1];
                t.Normalize();
                normal = Vector3.ProjectOnPlane(normal, t).normalized;
                frames[i] = (t, normal);
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 t = frames[i].t, nn = frames[i].n, bn = Vector3.Cross(t, nn);
                for (int s = 0; s <= segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    Vector3 dir = nn * Mathf.Cos(a) * flatten + bn * Mathf.Sin(a);
                    Vector3 nrm = (nn * Mathf.Cos(a) / Mathf.Max(.01f, flatten) + bn * Mathf.Sin(a)).normalized;
                    b.Vertex(path[i] + dir * radii[i], nrm, new Vector2(s / (float)segments, i / (float)(n - 1)));
                }
            }
            int ring = segments + 1;
            for (int i = 0; i < n - 1; i++)
                for (int s = 0; s < segments; s++) { int a = i * ring + s; b.QuadAuto(a, a + 1, a + ring + 1, a + ring); }
            if (caps)
                foreach (var (index, dirSign) in new[] { (0, -1f), (n - 1, 1f) })
                {
                    if (radii[index] < .0001f) continue;
                    Vector3 t = frames[index].t * dirSign, nn = frames[index].n, bn = Vector3.Cross(frames[index].t, nn);
                    int center = b.Vertex(path[index], t, new Vector2(.5f, .5f));
                    for (int s = 0; s <= segments; s++)
                    {
                        float a = s / (float)segments * Mathf.PI * 2f;
                        b.Vertex(path[index] + (nn * Mathf.Cos(a) * flatten + bn * Mathf.Sin(a)) * radii[index], t, new Vector2(.5f, .5f));
                    }
                    for (int s = 0; s < segments; s++) b.TriangleAuto(center, center + s + 1, center + s + 2);
                }
            return b.Build();
        }

        /// <summary>
        /// 凸多边形沿 Z 挤出并在正面倒角。<paramref name="outline"/> 在 XY 平面、逆时针；正面朝 +Z。
        /// </summary>
        public static Mesh Extrude(string name, IList<Vector2> outline, float depth, float bevel)
        {
            var b = new Builder(name);
            int n = outline.Count;
            Vector2 centroid = Vector2.zero; foreach (var p in outline) centroid += p; centroid /= n;
            Vector2 Inset(int i)
            {
                Vector2 prev = outline[(i - 1 + n) % n], cur = outline[i], next = outline[(i + 1) % n];
                Vector2 e0 = (cur - prev).normalized, e1 = (next - cur).normalized;
                Vector2 n0 = new(e0.y, -e0.x), n1 = new(e1.y, -e1.x);
                Vector2 bis = (n0 + n1).normalized; float cos = Mathf.Max(.3f, Vector2.Dot(bis, n0));
                return cur - bis * (bevel / cos);
            }
            float front = depth * .5f, back = -depth * .5f, bevelZ = front - bevel;
            // 正面
            int c0 = b.Vertex(new Vector3(centroid.x, centroid.y, front), Vector3.forward, centroid);
            for (int i = 0; i < n; i++) { var p = Inset(i); b.Vertex(new Vector3(p.x, p.y, front), Vector3.forward, p); }
            for (int i = 0; i < n; i++) b.TriangleAuto(c0, c0 + 1 + (i + 1) % n, c0 + 1 + i);
            // 背面
            int c1 = b.Vertex(new Vector3(centroid.x, centroid.y, back), Vector3.back, centroid);
            for (int i = 0; i < n; i++) b.Vertex(new Vector3(outline[i].x, outline[i].y, back), Vector3.back, outline[i]);
            for (int i = 0; i < n; i++) b.TriangleAuto(c1, c1 + 1 + i, c1 + 1 + (i + 1) % n);
            // 倒角与侧壁：每条边独立顶点，保持硬边
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector2 e = (outline[j] - outline[i]).normalized; var side = new Vector3(e.y, -e.x, 0);
                var bevelNormal = (side + Vector3.forward).normalized;
                Vector2 pi = Inset(i), pj = Inset(j);
                int a = b.Vertex(new Vector3(pi.x, pi.y, front), bevelNormal, pi);
                int bb = b.Vertex(new Vector3(pj.x, pj.y, front), bevelNormal, pj);
                int cc = b.Vertex(new Vector3(outline[j].x, outline[j].y, bevelZ), bevelNormal, outline[j]);
                int d = b.Vertex(new Vector3(outline[i].x, outline[i].y, bevelZ), bevelNormal, outline[i]);
                b.QuadAuto(a, d, cc, bb);
                int s0 = b.Vertex(new Vector3(outline[i].x, outline[i].y, bevelZ), side, outline[i]);
                int s1 = b.Vertex(new Vector3(outline[j].x, outline[j].y, bevelZ), side, outline[j]);
                int s2 = b.Vertex(new Vector3(outline[j].x, outline[j].y, back), side, outline[j]);
                int s3 = b.Vertex(new Vector3(outline[i].x, outline[i].y, back), side, outline[i]);
                b.QuadAuto(s0, s3, s2, s1);
            }
            return b.Build();
        }

        /// <summary>菱形截面的剑身，沿 +Z 延伸，末段收成剑尖。每个棱面独立顶点，棱线锐利。</summary>
        public static Mesh Blade(string name, float length, float width, float thickness, float tipLength, int steps = 10)
        {
            var b = new Builder(name);
            var rings = new List<Vector3[]>();
            for (int i = 0; i <= steps; i++)
            {
                float z = length * i / steps;
                float taper = z < length - tipLength ? Mathf.Lerp(1f, .86f, z / (length - tipLength)) : .86f * (length - z) / tipLength;
                float w = width * .5f * taper, t = thickness * .5f * Mathf.Max(.25f, taper);
                rings.Add(new[] { new Vector3(w, 0, z), new Vector3(0, t, z), new Vector3(-w, 0, z), new Vector3(0, -t, z) });
            }
            for (int f = 0; f < 4; f++)
            {
                int g = (f + 1) % 4;
                for (int i = 0; i < steps; i++)
                {
                    Vector3 a = rings[i][f], c = rings[i][g], d = rings[i + 1][g], e = rings[i + 1][f];
                    Vector3 normal = Vector3.Cross(c - a, e - a).normalized;
                    if (Vector3.Dot(normal, (a + c) * .5f - new Vector3(0, 0, a.z)) < 0) normal = -normal;
                    int i0 = b.Vertex(a, normal, new Vector2(f / 4f, i / (float)steps));
                    int i1 = b.Vertex(c, normal, new Vector2(g / 4f, i / (float)steps));
                    int i2 = b.Vertex(d, normal, new Vector2(g / 4f, (i + 1) / (float)steps));
                    int i3 = b.Vertex(e, normal, new Vector2(f / 4f, (i + 1) / (float)steps));
                    b.QuadFacing(i0, i1, i2, i3, normal);
                }
            }
            int baseCenter = b.Vertex(Vector3.zero, Vector3.back, Vector2.zero);
            for (int f = 0; f < 4; f++) b.Vertex(rings[0][f], Vector3.back, Vector2.zero);
            for (int f = 0; f < 4; f++) b.TriangleAuto(baseCenter, baseCenter + 1 + f, baseCenter + 1 + (f + 1) % 4);
            return b.Build();
        }

        /// <summary>圆环（甜甜圈），环面在 XZ 平面。</summary>
        public static Mesh Torus(string name, float major, float minor, int segments = 32, int sides = 10)
        {
            var b = new Builder(name);
            for (int i = 0; i <= segments; i++)
            {
                float u = i / (float)segments * Mathf.PI * 2f;
                var center = new Vector3(Mathf.Cos(u) * major, 0, Mathf.Sin(u) * major);
                for (int j = 0; j <= sides; j++)
                {
                    float v = j / (float)sides * Mathf.PI * 2f;
                    var n = new Vector3(Mathf.Cos(u) * Mathf.Cos(v), Mathf.Sin(v), Mathf.Sin(u) * Mathf.Cos(v));
                    b.Vertex(center + n * minor, n, new Vector2(i / (float)segments, j / (float)sides));
                }
            }
            int ring = sides + 1;
            for (int i = 0; i < segments; i++)
                for (int j = 0; j < sides; j++) { int a = i * ring + j; b.QuadAuto(a, a + 1, a + ring + 1, a + ring); }
            return b.Build();
        }

        /// <summary>双面布料：宽 <paramref name="width"/>、自顶向下长 <paramref name="height"/>，向 -Z 鼓起。</summary>
        public static Mesh Cloth(string name, float width, float height, float bulge, float flare, int columns = 10, int rows = 14,
            int folds = 0, float foldDepth = 0, float vFrom = 0, float vTo = 1, float inflate = 0)
        {
            var b = new Builder(name);
            Vector3 Point(float u, float v)
            {
                float w = width * (1 + flare * v);
                float x = (u - .5f) * w;
                float z = -bulge * Mathf.Sin(v * Mathf.PI * .5f) - .03f * Mathf.Cos((u - .5f) * Mathf.PI) * v;
                // 竖向褶皱自上而下逐渐加深，下摆处起伏最大。
                z -= foldDepth * Mathf.Sin(u * Mathf.PI * 2 * folds) * Mathf.Pow(v, 1.3f);
                return new Vector3(x, -v * height, z);
            }
            for (int side = 0; side < 2; side++)
            {
                int start = b.Count;
                for (int r = 0; r <= rows; r++)
                    for (int c = 0; c <= columns; c++)
                    {
                        float u = c / (float)columns, v = Mathf.Lerp(vFrom, vTo, r / (float)rows);
                        Vector3 p = Point(u, v), du = Point(Mathf.Min(1, u + .01f), v) - Point(Mathf.Max(0, u - .01f), v), dv = Point(u, Mathf.Min(1, v + .01f)) - Point(u, Mathf.Max(0, v - .01f));
                        var n = Vector3.Cross(dv, du).normalized; if (side == 1) n = -n;
                        // inflate 让同一曲面的一段（如下摆）包住原布料，不发生 Z 冲突。
                        b.Vertex(p + n * inflate, n, new Vector2(u, 1 - v));
                    }
                int w = columns + 1;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < columns; c++)
                    {
                        int a = start + r * w + c;
                        b.QuadAuto(a, a + 1, a + w + 1, a + w);
                    }
            }
            return b.Build();
        }

        /// <summary>平面圆环（法阵、光圈），位于 XZ 平面，UV.x 沿圆周、UV.y 由内到外。</summary>
        public static Mesh FlatRing(string name, float inner, float outer, int segments = 64)
        {
            var b = new Builder(name);
            for (int s = 0; s <= segments; s++)
            {
                float a = s / (float)segments * Mathf.PI * 2f; var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                b.Vertex(dir * inner, Vector3.up, new Vector2(s / (float)segments * 4, 0));
                b.Vertex(dir * outer, Vector3.up, new Vector2(s / (float)segments * 4, 1));
            }
            for (int s = 0; s < segments; s++) { int a = s * 2; b.QuadAuto(a, a + 2, a + 3, a + 1); }
            return b.Build();
        }

        /// <summary>朝 +Y 的方形面片，UV 0..1，用于贴图式特效与阴影。</summary>
        public static Mesh Quad(string name, float size)
        {
            var b = new Builder(name); float h = size * .5f;
            b.Vertex(new Vector3(-h, 0, -h), Vector3.up, new Vector2(0, 0)); b.Vertex(new Vector3(h, 0, -h), Vector3.up, new Vector2(1, 0));
            b.Vertex(new Vector3(h, 0, h), Vector3.up, new Vector2(1, 1)); b.Vertex(new Vector3(-h, 0, h), Vector3.up, new Vector2(0, 1));
            b.QuadAuto(0, 3, 2, 1); return b.Build();
        }

        /// <summary>三片正交面片，配合叠加柔光贴图，从任何角度看都是一团光。</summary>
        public static Mesh GlowCross(string name, float size)
        {
            var b = new Builder(name); float h = size * .5f;
            void Face(Vector3 u, Vector3 v, Vector3 n)
            {
                int i = b.Vertex(-u * h - v * h, n, new Vector2(0, 0)); b.Vertex(u * h - v * h, n, new Vector2(1, 0));
                b.Vertex(u * h + v * h, n, new Vector2(1, 1)); b.Vertex(-u * h + v * h, n, new Vector2(0, 1));
                b.QuadAuto(i, i + 3, i + 2, i + 1);
            }
            Face(Vector3.right, Vector3.forward, Vector3.up); Face(Vector3.right, Vector3.up, Vector3.back); Face(Vector3.forward, Vector3.up, Vector3.right);
            return b.Build();
        }

        internal sealed class Builder
        {
            private readonly string _name;
            private readonly List<Vector3> _v = new(), _n = new();
            private readonly List<Vector2> _uv = new();
            private readonly List<int> _t = new();
            public Builder(string name) => _name = name;
            public int Count => _v.Count;
            public int Vertex(Vector3 p, Vector3 n, Vector2 uv) { _v.Add(p); _n.Add(n); _uv.Add(uv); return _v.Count - 1; }
            public void Triangle(int a, int b, int c) { _t.Add(a); _t.Add(b); _t.Add(c); }
            public void Quad(int a, int b, int c, int d) { Triangle(a, b, c); Triangle(a, c, d); }
            /// <summary>按期望法线决定绕序，免去手算朝向。</summary>
            public void QuadFacing(int a, int b, int c, int d, Vector3 normal)
            {
                if (Vector3.Dot(Vector3.Cross(_v[b] - _v[a], _v[c] - _v[a]), normal) >= 0) Quad(a, b, c, d); else Quad(a, d, c, b);
            }
            /// <summary>按顶点法线自动定绕序（Unity 以顺时针为正面）。</summary>
            public void QuadAuto(int a, int b, int c, int d) => QuadFacing(a, b, c, d, _n[a] + _n[b] + _n[c] + _n[d]);
            public void TriangleAuto(int a, int b, int c)
            {
                if (Vector3.Dot(Vector3.Cross(_v[b] - _v[a], _v[c] - _v[a]), _n[a] + _n[b] + _n[c]) >= 0) Triangle(a, b, c); else Triangle(a, c, b);
            }
            public Mesh Build()
            {
                var mesh = new Mesh { name = _name };
                if (_v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(_v); mesh.SetNormals(_n); mesh.SetUVs(0, _uv); mesh.SetTriangles(_t, 0);
                mesh.RecalculateBounds(); mesh.RecalculateTangents();
                return mesh;
            }
        }
    }
}
