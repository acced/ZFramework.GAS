using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public interface ICuePreview : IDisposable
    {
        string Tag { get; }
        void SetActive(GameObject previewInstance, bool active, float elapsed);
    }
    public sealed class PreviewPanel : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly IMGUIContainer _viewport;
        private PreviewRenderUtility _preview;
        private GameObject _source, _instance;
        private IPoseSampler _sampler;
        private CueVfxPreview _vfx;
        private readonly List<ICuePreview> _cues = new();
        // 略微俯视：既看得到角色正面，也看得到贴地的法阵与光环。
        private Vector2 _orbit = new(145, 14);
        private float _distance = 4;
        private Bounds _bounds;
        private string _error, _cueWarning;
        private readonly Label _caption;
        public PreviewPanel(SkillDocument doc)
        {
            _doc = doc; AddToClassList("preview-panel");
            var header = new Label("预览视口"); header.AddToClassList("panel-heading"); Add(header);
            _viewport = new IMGUIContainer(Draw) { style = { flexGrow = 1, minHeight = 150 } }; Add(_viewport);
            _caption = new Label("拖动旋转 · 滚轮缩放 · 双击聚焦"); _caption.AddToClassList("preview-caption"); Add(_caption);
            doc.Playback.Changed += Sample;
            doc.Changed += Changed;
            EditorApplication.projectChanged += ProjectChanged;
        }
        private void ProjectChanged() { if (_source != null) Load(); }
        private void Changed(ChangeSet change)
        {
            if (_source != _doc.PreviewPrefab) Load();
            else if (_instance != null) { _vfx?.Dispose(); _vfx = new CueVfxPreview(_instance); }
            Sample();
        }
        public void Load()
        {
            Cleanup(); _source = _doc.PreviewPrefab; _error = null;
            if (_source == null) return;
            try
            {
                _preview = new PreviewRenderUtility();
                PreviewStage.Configure(_preview);
                _instance = _preview.InstantiatePrefabInScene(_source);
                _instance.hideFlags = HideFlags.HideAndDontSave;
                // 丢失脚本的组件以 null 出现，跳过即可，不应让整个预览失败。
                foreach (var behaviour in _instance.GetComponentsInChildren<MonoBehaviour>(true)) if (behaviour != null) behaviour.enabled = false;
                foreach (var animator in _instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                foreach (var animation in _instance.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
                _sampler = AnimationAdapterRegistry.Resolve(_source).BeginPreview(_instance);
                _vfx = new CueVfxPreview(_instance);
                _bounds = new Bounds(_instance.transform.position, Vector3.one);
                var renderers = _instance.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0) { _bounds = renderers[0].bounds; foreach (var r in renderers) _bounds.Encapsulate(r.bounds); }
                _distance = Mathf.Max(1, _bounds.size.magnitude * 1.7f);
                PreviewStage.AddGround(_preview, _bounds);
                foreach (var type in TypeCache.GetTypesDerivedFrom<ICuePreview>())
                    if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null) _cues.Add((ICuePreview)Activator.CreateInstance(type));
                Sample();
            }
            catch (Exception ex) { _error = ex.Message; Cleanup(); }
        }
        private void Sample()
        {
            _cueWarning = null;
            var animation = _doc.Root?.Animation;
            if (_sampler != null && animation != null)
            {
                try
                {
                    var clips = AnimationAdapterRegistry.Resolve(_source).EnumerateClips(_source);
                    float natural = animation.Duration;
                    foreach (var clip in clips) if (clip.Name == animation.ClipName) { natural = clip.Length; break; }
                    void Pose(float time) => _sampler.Sample(animation.ClipName, time / Mathf.Max(.001f, animation.Duration) * natural);
                    Pose(_doc.Playback.Time);
                    _vfx?.Sample(animation, _doc.Playback.Time, Pose);
                    var so = _doc.Serialized(animation); var regions = so.FindProperty("cueRegions");
                    for (int i = 0; i < regions.arraySize; i++)
                    {
                        var region = regions.GetArrayElementAtIndex(i);
                        float start = region.FindPropertyRelative("start").floatValue, end = region.FindPropertyRelative("end").floatValue;
                        if (_doc.Playback.Time < start || end >= 0 && _doc.Playback.Time >= end) continue;
                        string tag = region.FindPropertyRelative("cueTag.m_Name").stringValue;
                        if (_cues.Exists(cue => cue.Tag == tag)) continue;
                        _cueWarning = _vfx?.ConfigurationWarning(tag);
                        if (_cueWarning != null) break;
                    }
                    foreach (var cue in _cues)
                    {
                        bool active = false; float elapsed = 0;
                        for (int i = 0; i < regions.arraySize; i++)
                        {
                            var r = regions.GetArrayElementAtIndex(i); float start = r.FindPropertyRelative("start").floatValue, end = r.FindPropertyRelative("end").floatValue;
                            if (r.FindPropertyRelative("cueTag.m_Name").stringValue == cue.Tag && _doc.Playback.Time >= start && (end < 0 || _doc.Playback.Time < end))
                            { active = true; elapsed = _doc.Playback.Time - start; }
                        }
                        cue.SetActive(_instance, active, elapsed);
                    }
                }
                catch (Exception ex) { _error = ex.Message; }
            }
            else _vfx?.Dispose();
            _caption.text = _cueWarning ?? "拖动旋转 · 滚轮缩放 · 双击聚焦";
            _caption.tooltip = _cueWarning;
            _viewport.MarkDirtyRepaint();
        }
        private void Draw()
        {
            Rect rect = GUILayoutUtility.GetRect(10, 10000, 150, 10000);
            if (rect.width <= 1 || rect.height <= 1) return;
            if (_preview == null)
            {
                EditorGUI.DrawRect(rect, SkillColors.Hex("#1d2026"));
                GUI.Label(rect, _error ?? "选择预览角色\n拖动播放头查看动画姿态", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true, fontSize = 12 });
                return;
            }
            var evt = Event.current;
            if (rect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.MouseDrag && evt.button == 0) { _orbit += evt.delta * .6f; evt.Use(); _viewport.MarkDirtyRepaint(); }
                if (evt.type == EventType.ScrollWheel) { _distance = Mathf.Clamp(_distance * (1 + evt.delta.y * .05f), .2f, 100); evt.Use(); _viewport.MarkDirtyRepaint(); }
                if (evt.type == EventType.MouseDown && evt.clickCount == 2) { _distance = Mathf.Max(1, _bounds.size.magnitude * 1.7f); evt.Use(); }
            }
            if (evt.type != EventType.Repaint) return;
            _preview.BeginPreview(rect, GUIStyle.none);
            var rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0);
            _preview.camera.transform.position = _bounds.center + rotation * new Vector3(0, 0, -_distance);
            _preview.camera.transform.rotation = rotation;
            _preview.camera.nearClipPlane = .01f; _preview.camera.farClipPlane = 200;
            _preview.Render(true);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
            string caption = _error ?? _cueWarning ?? (_doc.Root?.Animation?.ClipName ?? "角色预览") + $"  @ {_doc.Playback.Time:0.00}s";
            GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, 22), caption, EditorStyles.whiteMiniLabel);
            var animationAsset = _doc.Root?.Animation;
            if (animationAsset == null) return;
            var regions = _doc.Serialized(animationAsset).FindProperty("cueRegions"); int line = 0;
            for (int i = 0; i < regions.arraySize; i++)
            {
                var r = regions.GetArrayElementAtIndex(i); float start = r.FindPropertyRelative("start").floatValue, end = r.FindPropertyRelative("end").floatValue;
                if (_doc.Playback.Time >= start && (end < 0 || _doc.Playback.Time < end))
                    GUI.Label(new Rect(rect.x + 8, rect.y + 28 + line++ * 18, rect.width - 16, 18), "▶ " + r.FindPropertyRelative("cueTag.m_Name").stringValue, EditorStyles.whiteMiniLabel);
            }
        }
        private void Cleanup()
        {
            _vfx?.Dispose(); _vfx = null;
            foreach (var cue in _cues) { try { cue.Dispose(); } catch (Exception e) { Debug.LogException(e); } } _cues.Clear();
            try { _sampler?.Dispose(); } finally { _sampler = null; _preview?.Cleanup(); _preview = null; _instance = null; }
        }
        public void Dispose() { _doc.Playback.Changed -= Sample; _doc.Changed -= Changed; EditorApplication.projectChanged -= ProjectChanged; Cleanup(); }
    }

    /// <summary>预览舞台：灯光、环境光与脚下的地面和接触阴影。预览面板与外部截图工具共用，保证看到的一致。</summary>
    public static class PreviewStage
    {
        private static Mesh _quad;
        private static Material _floor, _shadow;

        public static void Configure(PreviewRenderUtility preview)
        {
            preview.camera.fieldOfView = 30;
            preview.camera.clearFlags = CameraClearFlags.Color;
            preview.camera.backgroundColor = SkillColors.Hex("#1d2026");
            preview.ambientColor = new Color(.22f, .24f, .29f, 1);
            // 角色面朝 +Z：主光从正面右上方打下，冷色补光从左前方补，避免正面只吃补光而发暗。
            preview.lights[0].intensity = 1.1f; preview.lights[0].color = new Color(1f, .95f, .88f);
            preview.lights[0].transform.rotation = Quaternion.Euler(38, 205, 0);
            preview.lights[1].intensity = .55f; preview.lights[1].color = new Color(.72f, .82f, 1f);
            preview.lights[1].transform.rotation = Quaternion.Euler(14, 115, 0);
        }

        /// <summary>按包围盒在脚下放一块柔边地面与接触阴影。所需着色器缺失（如 SRP 工程）时跳过。</summary>
        public static void AddGround(PreviewRenderUtility preview, Bounds bounds)
        {
            if (!EnsureResources()) return;
            float footprint = Mathf.Max(bounds.extents.x, bounds.extents.z);
            var floorSize = Mathf.Max(2.4f, bounds.size.y * 1.6f);
            preview.AddSingleGO(Quad("PreviewFloor", _floor, new Vector3(bounds.center.x, bounds.min.y, bounds.center.z), floorSize));
            preview.AddSingleGO(Quad("PreviewShadow", _shadow, new Vector3(bounds.center.x, bounds.min.y + .002f, bounds.center.z), Mathf.Max(.8f, footprint * 2.6f)));
        }

        private static GameObject Quad(string name, Material material, Vector3 position, float size)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = position; go.transform.localScale = new Vector3(size, 1, size);
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        private static bool EnsureResources()
        {
            if (_quad != null && _floor != null && _shadow != null) return true;
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null) return false;
            _quad = new Mesh { name = "PreviewGroundQuad", hideFlags = HideFlags.HideAndDontSave };
            _quad.SetVertices(new[] { new Vector3(-.5f, 0, -.5f), new Vector3(-.5f, 0, .5f), new Vector3(.5f, 0, .5f), new Vector3(.5f, 0, -.5f) });
            _quad.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            _quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0); _quad.RecalculateNormals(); _quad.RecalculateBounds();
            _floor = Material("PreviewFloor", shader, Radial(128, r =>
            {
                // 略亮于背景的圆形台面，外缘柔化，带一圈细环增加层次。
                float edge = Mathf.Clamp01((1 - r) / .25f);
                float ring = Mathf.Exp(-Mathf.Pow((r - .78f) / .012f, 2)) * .5f;
                return new Color(.2f, .23f, .29f, Mathf.Clamp01(edge * .85f + ring * edge));
            }));
            _shadow = Material("PreviewShadow", shader, Radial(64, r => new Color(0, 0, 0, Mathf.Pow(Mathf.Clamp01(1 - r), 1.8f) * .75f)));
            return true;
        }

        private static Material Material(string name, Shader shader, Texture2D texture)
        {
            var material = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave, mainTexture = texture };
            material.SetColor("_TintColor", new Color(.5f, .5f, .5f, .5f));
            return material;
        }

        private static Texture2D Radial(int size, Func<float, Color> shade)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + .5f) / size * 2 - 1, v = (y + .5f) / size * 2 - 1;
                    pixels[y * size + x] = shade(Mathf.Min(1, Mathf.Sqrt(u * u + v * v)));
                }
            texture.SetPixels(pixels); texture.Apply(); return texture;
        }
    }
}
