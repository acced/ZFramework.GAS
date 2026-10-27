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
        private readonly List<ICuePreview> _cues = new();
        private Vector2 _orbit = new(145, -10);
        private float _distance = 4;
        private Bounds _bounds;
        private string _error;
        private readonly Label _caption;
        public PreviewPanel(SkillDocument doc)
        {
            _doc = doc; AddToClassList("preview-panel");
            var header = new Label("预览视口"); header.AddToClassList("panel-heading"); Add(header);
            _viewport = new IMGUIContainer(Draw) { style = { flexGrow = 1, minHeight = 150 } }; Add(_viewport);
            _caption = new Label("拖动旋转 · 滚轮缩放 · 双击聚焦"); _caption.AddToClassList("preview-caption"); Add(_caption);
            doc.Playback.Changed += Sample;
            doc.Changed += Changed;
        }
        private void Changed(ChangeSet change)
        {
            if (_source != _doc.PreviewPrefab) Load();
            Sample();
        }
        public void Load()
        {
            Cleanup(); _source = _doc.PreviewPrefab; _error = null;
            if (_source == null) return;
            try
            {
                _preview = new PreviewRenderUtility();
                _preview.camera.fieldOfView = 30;
                _preview.camera.clearFlags = CameraClearFlags.Color;
                _preview.camera.backgroundColor = SkillColors.Hex("#1d2026");
                _preview.lights[0].intensity = 1.2f; _preview.lights[0].transform.rotation = Quaternion.Euler(40, 40, 0);
                _preview.lights[1].intensity = .7f;
                _instance = _preview.InstantiatePrefabInScene(_source);
                _instance.hideFlags = HideFlags.HideAndDontSave;
                foreach (var behaviour in _instance.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                foreach (var animator in _instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                foreach (var animation in _instance.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
                _sampler = AnimationAdapterRegistry.Resolve(_source).BeginPreview(_instance);
                _bounds = new Bounds(_instance.transform.position, Vector3.one);
                var renderers = _instance.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0) { _bounds = renderers[0].bounds; foreach (var r in renderers) _bounds.Encapsulate(r.bounds); }
                _distance = Mathf.Max(1, _bounds.size.magnitude * 1.7f);
                foreach (var type in TypeCache.GetTypesDerivedFrom<ICuePreview>())
                    if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null) _cues.Add((ICuePreview)Activator.CreateInstance(type));
                Sample();
            }
            catch (Exception ex) { _error = ex.Message; Cleanup(); }
        }
        private void Sample()
        {
            var animation = _doc.Root?.Animation;
            if (_sampler != null && animation != null)
            {
                try
                {
                    var clips = AnimationAdapterRegistry.Resolve(_source).EnumerateClips(_source);
                    float natural = animation.Duration;
                    foreach (var clip in clips) if (clip.Name == animation.ClipName) { natural = clip.Length; break; }
                    _sampler.Sample(animation.ClipName, _doc.Playback.Time / Mathf.Max(.001f, animation.Duration) * natural);
                    var so = _doc.Serialized(animation); var regions = so.FindProperty("cueRegions");
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
            string caption = _error ?? (_doc.Root?.Animation?.ClipName ?? "角色预览") + $"  @ {_doc.Playback.Time:0.00}s";
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
            foreach (var cue in _cues) { try { cue.Dispose(); } catch (Exception e) { Debug.LogException(e); } } _cues.Clear();
            try { _sampler?.Dispose(); } finally { _sampler = null; _preview?.Cleanup(); _preview = null; _instance = null; }
        }
        public void Dispose() { _doc.Playback.Changed -= Sample; _doc.Changed -= Changed; Cleanup(); }
    }
}
