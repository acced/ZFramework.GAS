using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>示例美术资产的落盘：材质、贴图与集中存放的网格。</summary>
    internal sealed class SampleArt
    {
        private readonly string _folder;
        private Mesh _meshContainer;
        private readonly Dictionary<string, Material> _materials = new();

        public string Folder => _folder;

        public SampleArt(string folder)
        {
            _folder = folder;
            EnsureFolder(folder);
            // 所有网格存成一个资产的子对象，Project 面板里只占一行。
            _meshContainer = new Mesh { name = "BasicCombat Meshes" };
            AssetDatabase.CreateAsset(_meshContainer, folder + "/Meshes.asset");
        }

        /// <summary>
        /// 切换场景会卸载未被引用的资产，之前拿到的对象随之失效；按路径重新取回缓存的网格容器与材质。
        /// </summary>
        public void Refresh()
        {
            _meshContainer = AssetDatabase.LoadAssetAtPath<Mesh>(_folder + "/Meshes.asset");
            foreach (var name in new List<string>(_materials.Keys))
                _materials[name] = AssetDatabase.LoadAssetAtPath<Material>(_folder + "/" + name + ".mat");
        }

        public Mesh Save(Mesh mesh)
        {
            AssetDatabase.AddObjectToAsset(mesh, _meshContainer);
            return mesh;
        }

        public Material Opaque(string name, string hex, float metallic = 0, float smoothness = .3f, string emissionHex = null, float emission = 0)
        {
            if (_materials.TryGetValue(name, out var cached)) return cached;
            var material = new Material(Shader.Find("Standard")) { name = name, color = Hex(hex) };
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Glossiness", smoothness);
            if (emissionHex != null)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Hex(emissionHex) * emission);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            AssetDatabase.CreateAsset(material, _folder + "/" + name + ".mat");
            return _materials[name] = material;
        }

        /// <summary>叠加发光材质，用于剑光、光环与法阵。颜色由顶点色与 MaterialPropertyBlock 控制。</summary>
        public Material Additive(string name, Texture2D texture, string tintHex)
        {
            if (_materials.TryGetValue(name, out var cached)) return cached;
            var material = new Material(Shader.Find("Legacy Shaders/Particles/Additive")) { name = name, mainTexture = texture };
            material.SetColor("_TintColor", Hex(tintHex));
            AssetDatabase.CreateAsset(material, _folder + "/" + name + ".mat");
            return _materials[name] = material;
        }

        /// <summary>半透明（Alpha 混合）材质，用于脚下的接触阴影。</summary>
        public Material AlphaBlended(string name, Texture2D texture, string tintHex)
        {
            if (_materials.TryGetValue(name, out var cached)) return cached;
            var material = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended")) { name = name, mainTexture = texture };
            material.SetColor("_TintColor", Hex(tintHex));
            AssetDatabase.CreateAsset(material, _folder + "/" + name + ".mat");
            return _materials[name] = material;
        }

        /// <summary>径向柔光贴图。<paramref name="ring"/> 大于 0 时生成以该半径为峰的光环。</summary>
        public Texture2D RadialTexture(string name, int size, float ring = 0, float width = .18f, int spokes = 0)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + .5f) / size * 2 - 1, v = (y + .5f) / size * 2 - 1, r = Mathf.Sqrt(u * u + v * v);
                    float value = ring <= 0 ? Mathf.Pow(Mathf.Clamp01(1 - r), 2.2f)
                        : Mathf.Exp(-Mathf.Pow((r - ring) / width, 2)) + .35f * Mathf.Exp(-Mathf.Pow((r - ring * .72f) / (width * .35f), 2));
                    if (spokes > 0 && ring > 0 && r < ring * .9f && r > ring * .2f)
                    {
                        float angle = Mathf.Atan2(v, u) * spokes / (2 * Mathf.PI);
                        float line = Mathf.Exp(-Mathf.Pow((angle - Mathf.Round(angle)) * 9, 2));
                        value += .45f * line * Mathf.Clamp01((r - ring * .2f) * 4);
                    }
                    if (r > 1) value = 0;
                    value = Mathf.Clamp01(value);
                    pixels[y * size + x] = new Color(1, 1, 1, value) * new Color(value, value, value, 1);
                }
            texture.SetPixels(pixels); texture.Apply(true);
            AssetDatabase.CreateAsset(texture, _folder + "/" + name + ".asset");
            return texture;
        }

        /// <summary>沿 U 方向渐隐、V 方向两侧柔化的条带贴图，用于剑光拖尾。</summary>
        public Texture2D StreakTexture(string name, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float u = (x + .5f) / width, v = (y + .5f) / height;
                    // v = 1 是剑尖一侧：亮而锐利；靠近剑格一侧淡出。
                    float across = Mathf.Pow(Mathf.Clamp01(v), 1.6f) * Mathf.Clamp01((1 - v) * 14);
                    float core = Mathf.Exp(-Mathf.Pow((v - .86f) / .07f, 2));
                    float value = Mathf.Clamp01((across * .75f + core) * Mathf.Pow(u, .7f));
                    pixels[y * width + x] = new Color(value, value, value, value);
                }
            texture.SetPixels(pixels); texture.Apply(true);
            AssetDatabase.CreateAsset(texture, _folder + "/" + name + ".asset");
            return texture;
        }

        /// <summary>法阵：多重同心环、内接六芒星、刻度与符文点。</summary>
        public Texture2D MagicCircleTexture(string name, int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            var star = new List<(Vector2 a, Vector2 b)>();
            for (int tri = 0; tri < 2; tri++)
                for (int k = 0; k < 3; k++)
                {
                    float a0 = Mathf.PI / 2 + tri * Mathf.PI + k * Mathf.PI * 2 / 3, a1 = a0 + Mathf.PI * 2 / 3;
                    star.Add((new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * .8f, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * .8f));
                }
            float Ring(float r, float at, float w) => Mathf.Exp(-Mathf.Pow((r - at) / w, 2));
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + .5f) / size * 2 - 1, (y + .5f) / size * 2 - 1);
                    float r = p.magnitude, angle = Mathf.Atan2(p.y, p.x);
                    float value = Ring(r, .95f, .012f) + Ring(r, .88f, .008f) * .9f + Ring(r, .8f, .006f) * .7f + Ring(r, .5f, .007f) * .6f + Ring(r, .2f, .01f) * .7f;
                    // 外圈刻度
                    if (r > .885f && r < .945f) { float tick = angle / (Mathf.PI * 2) * 72; value += .7f * Mathf.Exp(-Mathf.Pow((tick - Mathf.Round(tick)) * 6, 2)); }
                    // 六芒星
                    foreach (var (a, b) in star)
                    {
                        Vector2 ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                        value += .85f * Mathf.Exp(-Mathf.Pow((p - (a + ab * t)).magnitude / .008f, 2));
                    }
                    // 符文点
                    for (int k = 0; k < 12; k++)
                    {
                        float ak = k * Mathf.PI / 6 + Mathf.PI / 12; var c = new Vector2(Mathf.Cos(ak), Mathf.Sin(ak)) * .655f;
                        float d = (p - c).magnitude; value += Mathf.Exp(-Mathf.Pow((d - .035f) / .006f, 2)) * .8f + Mathf.Exp(-Mathf.Pow(d / .012f, 2)) * .6f;
                    }
                    value += .22f * Mathf.Pow(Mathf.Clamp01(1 - r), 1.5f);
                    if (r > .99f) value = 0;
                    value = Mathf.Clamp01(value);
                    pixels[y * size + x] = new Color(value, value, value, value);
                }
            texture.SetPixels(pixels); texture.Apply(true);
            AssetDatabase.CreateAsset(texture, _folder + "/" + name + ".asset");
            return texture;
        }

        /// <summary>光柱：自下而上淡出，夹带竖向能量纹。</summary>
        public Texture2D ColumnTexture(string name, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float u = (x + .5f) / width, v = (y + .5f) / height;
                    float streaks = .55f + .45f * Mathf.Pow(Mathf.Abs(Mathf.Sin(u * Mathf.PI * 9 + Mathf.Sin(u * 31) * .6f)), 3);
                    float value = Mathf.Clamp01(Mathf.Pow(1 - v, 1.8f) * streaks * Mathf.Clamp01(v * 30 + .3f));
                    pixels[y * width + x] = new Color(value, value, value, value);
                }
            texture.SetPixels(pixels); texture.Apply(true);
            AssetDatabase.CreateAsset(texture, _folder + "/" + name + ".asset");
            return texture;
        }

        public static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var color) ? color : Color.magenta;

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        /// <summary>挂一个网格部件。部件没有碰撞体，只是骨骼下的视觉。</summary>
        public static GameObject Part(Transform parent, string name, Mesh mesh, Material material,
            Vector3 position, Vector3 euler = default, Vector3? scale = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale ?? Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return go;
        }

        public static Transform Bone(Transform parent, string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            return go.transform;
        }
    }
}
