using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>Isolated pose, VFX, audio and query geometry. Does not simulate gameplay.</summary>
    public sealed class PreviewPanel : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly IMGUIContainer _viewport;
        private readonly Label _caption;
        private PreviewRenderUtility _preview;
        private GameObject _source, _instance;
        private UnityEngine.Object _clip;
        private IAnimationPreviewSampler _sampler;
        private CueVfxPreview _vfx;
        private SkillPreviewTools _tools;
        private bool _ghosts, _sound;
        private int _level = 1;
        private float _targetDistance = 2, _distance = 4;
        private TargetShape? _range;
        private SkillOrigin _rangeOrigin;
        private string _rangeNote, _error, _samplerError;
        private Vector2 _orbit = new(145, 14);
        private Bounds _bounds, _contentBounds;

        public PreviewPanel(SkillDocument doc)
        {
            _doc = doc;
            AddToClassList("preview-panel"); style.minHeight = 280;
            var header = new Label("预览视口"); header.AddToClassList("panel-heading"); Add(header);
            var options = new Foldout { text = "预览选项", value = false }; Add(options);
            var ghosts = new Toggle("前后帧"); options.Add(ghosts);
            ghosts.RegisterValueChangedCallback(e => { _ghosts = e.newValue; Sample(); });
            var sound = new Toggle("音效"); options.Add(sound);
            sound.RegisterValueChangedCallback(e => { _sound = e.newValue; Sample(); });
            var distance = new FloatField("目标点距离") { value = _targetDistance }; options.Add(distance);
            distance.RegisterValueChangedCallback(e => { _targetDistance = Mathf.Max(0, e.newValue); Sample(); });
            var level = new IntegerField("参数等级") { value = _level }; options.Add(level);
            level.RegisterValueChangedCallback(e => { _level = Mathf.Max(1, e.newValue); RefreshSelection(); });
            _viewport = new IMGUIContainer(Draw) { style = { flexGrow = 1, flexBasis = 0, minHeight = 35 } }; Add(_viewport);
            _caption = new Label(); _caption.AddToClassList("preview-caption"); Add(_caption);
            doc.Playback.Changed += Sample;
            doc.Changed += Changed; doc.Selection.Changed += RefreshSelection;
            EditorApplication.projectChanged += ProjectChanged;
        }

        private void RefreshSelection()
        {
            _range = null; _rangeNote = null;
            try { _range = SelectedActionRange.Read(_doc, _level, out _rangeOrigin, out _rangeNote); }
            catch (Exception error) { _rangeNote = error.Message; }
            Sample();
        }
        private void ProjectChanged() => Load();
        private void Changed(ChangeSet change)
        {
            if (_source != _doc.PreviewPrefab || _clip != _doc.Root?.Animation()?.Clip) Load();
            else RefreshSelection();
        }
        public void Load()
        {
            Cleanup(); _source = _doc.PreviewPrefab; _clip = _doc.Root?.Animation()?.Clip;
            _error = null; _samplerError = null;
            _preview = new PreviewRenderUtility();
            PreviewStage.Configure(_preview);
            if (_source != null) _instance = _preview.InstantiatePrefabInScene(_source);
            else { _instance = new GameObject("Skill Origin"); _preview.AddSingleGO(_instance); }
            _instance.hideFlags = HideFlags.HideAndDontSave;
            foreach (var behaviour in _instance.GetComponentsInChildren<Behaviour>(true))
                if (behaviour != null) behaviour.enabled = false;
            if (_clip != null)
            {
                try
                {
                    AnimationClipEditors.GetDuration(_clip);
                    AnimationClipEditors.TryGet(_clip, out var editor);
                    _sampler = editor.CreatePreview(_instance, _clip);
                    _sampler.Sample(0);
                }
                catch (Exception error) { _samplerError = error.Message; }
            }
            _vfx = new CueVfxPreview(_instance);
            _tools = new SkillPreviewTools();
            _bounds = new Bounds(_instance.transform.position, Vector3.one);
            var renderers = _instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                _bounds = renderers[0].bounds;
                foreach (var renderer in renderers) _bounds.Encapsulate(renderer.bounds);
            }
            _contentBounds = _bounds; _distance = Mathf.Max(1, _bounds.size.magnitude * 1.7f);
            PreviewStage.AddGround(_preview, _bounds);
            RefreshSelection();
        }
        private void Sample()
        {
            if (_instance == null) return;
            _error = _samplerError;
            var animation = _doc.Root?.Animation();
            void Pose(float time) => _sampler?.Sample(animation != null ? time / animation.Duration : 0);
            try
            {
                if (_samplerError == null)
                {
                    _tools.Rebuild(_instance, _doc, Pose, _ghosts, DrawRanges);
                    _tools.Audio(animation, _doc.Playback.Time, _sound && _doc.Playback.Playing, _doc.Playback.SeekVersion, _doc.Playback.Speed);
                    _vfx.Sample(animation, _doc.Playback.Time, Pose);
                }
            }
            catch (Exception error) { _error = error.Message; _tools.Audio(null, 0, false); }
            _caption.text = _error ?? _rangeNote ?? "拖动旋转 · 滚轮缩放 · 双击聚焦";
            _viewport.MarkDirtyRepaint();
        }
        private void DrawRanges()
        {
            _contentBounds = _bounds;
            var source = _instance.transform;
            if (_range is TargetShape shape)
            {
                var center = _rangeOrigin == SkillOrigin.Target || _rangeOrigin == SkillOrigin.TargetPoint
                    ? source.TransformPoint(Vector3.forward * _targetDistance) : source.position;
                _tools.Query(shape, source, center);
                _contentBounds.Encapsulate(new Bounds(center, Vector3.one * (2 * Mathf.Max(shape.Radius, shape.HalfExtents.magnitude))));
            }
            var animation = _doc.Root?.Animation();
            if (animation == null) return;
            var values = new EffectEvaluationContext(_level, _doc.Root.ResolveParameters(_level));
            foreach (var window in animation.hitWindows)
            {
                if (_doc.Playback.Time < window.start || _doc.Playback.Time >= window.end) continue;
                var query = window.targets.Build();
                if (query.Kind == TargetQueryKind.Hitbox && SkillColliderVolume.TryRead(_instance.GetComponent<SkillHitboxes>()?.Find(query.SourceKey), out var weapon))
                    _tools.Hitbox(weapon);
                else if (TargetQuery.IsVolume(query.Kind) && query.EffectiveOrigin == SkillOrigin.Caster)
                    _tools.Query(query.ResolveShape(values), source, source.position + source.rotation * window.offset, window.yaw);
            }
        }
        private void Draw()
        {
            var rect = GUILayoutUtility.GetRect(10, 10000, 35, 10000);
            if (_preview == null || rect.width <= 1 || rect.height <= 1) return;
            var evt = Event.current;
            if (rect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.MouseDrag && evt.button == 0) { _orbit += evt.delta * .6f; evt.Use(); _viewport.MarkDirtyRepaint(); }
                if (evt.type == EventType.ScrollWheel) { _distance = Mathf.Clamp(_distance * (1 + evt.delta.y * .05f), .2f, 100); evt.Use(); _viewport.MarkDirtyRepaint(); }
                if (evt.type == EventType.MouseDown && evt.clickCount == 2) { _distance = Mathf.Max(1, _bounds.size.magnitude * 1.7f); evt.Use(); }
            }
            if (evt.type != EventType.Repaint) return;
            _preview.BeginPreview(rect, GUIStyle.none); _tools.Draw(_preview);
            var rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0);
            _preview.camera.transform.position = _contentBounds.center + rotation * new Vector3(0, 0, -_distance);
            _preview.camera.transform.rotation = rotation;
            _preview.camera.nearClipPlane = .01f; _preview.camera.farClipPlane = 200;
            _preview.Render(true);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, 22),
                _error ?? $"{(_clip != null ? _clip.name : "时间轴预览")}  @ {_doc.Playback.Time:0.00}s", EditorStyles.whiteMiniLabel);
        }
        private void Cleanup()
        {
            _tools?.Dispose(); _tools = null;
            _vfx?.Dispose(); _vfx = null;
            _sampler?.Dispose(); _sampler = null; _clip = null;
            _preview?.Cleanup(); _preview = null; _instance = null;
        }
        public void Dispose()
        {
            _doc.Playback.Changed -= Sample; _doc.Changed -= Changed; _doc.Selection.Changed -= RefreshSelection;
            EditorApplication.projectChanged -= ProjectChanged; Cleanup();
        }
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
