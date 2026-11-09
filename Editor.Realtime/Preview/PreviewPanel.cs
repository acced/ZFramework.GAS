using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly DropdownField _sectionFrom=new DropdownField("跳入预演 · 来源段");
        private readonly List<AnimationSection> _previewSections=new();
        private readonly DropdownField _preClip = new DropdownField("前置动画");
        private IReadOnlyList<ClipInfo> _clips = Array.Empty<ClipInfo>();
        private CueVfxPreview _vfx;
        private SkillPreviewTools _tools;
        private SkillPreviewSimulation _simulation;
        private DummyLayout _layout=DummyLayout.扇形;
        private int _dummyCount=3,_aimIndex=2;
        private string _sourceTags="",_targetTags=""; private float _sourceValue,_targetValue;
        private GameObject _dummyPrefab;
        private AnimationClip _reaction;
        private SkillClipOverrideSet _override;
        private readonly Foldout _targetTools = new() { text = "目标预演", value = false };
        private readonly Foldout _animationTools = new() { text = "动画辅助", value = false };
        private readonly Toggle _rootMotion = new("根位移");
        private bool _simulate, _ghosts, _sound;
        private TargetShape? _range;
        private SkillSpawnPosition _rangeOrigin;
        private string _rangeNote;
        private bool NeedsOrigin => _doc.Root != null && (_targetTools.value || _range.HasValue);
        private float _targetDistance = 2;
        private int _level = 1;
        private readonly List<ICuePreview> _cues = new();
        // 略微俯视：既看得到角色正面，也看得到贴地的法阵与光环。
        private Vector2 _orbit = new(145, 14);
        private float _distance = 4;
        private Bounds _bounds, _contentBounds;
        private string _error, _cueWarning;
        private readonly Label _caption;
        public PreviewPanel(SkillDocument doc)
        {
            _doc = doc; AddToClassList("preview-panel"); style.minHeight = 280;
            var header = new Label("预览视口"); header.AddToClassList("panel-heading"); Add(header);
            var bar = new VisualElement { style = { flexDirection = FlexDirection.Row, flexShrink = 0 } };
            Add(bar); bar.Add(_animationTools); bar.Add(_targetTools);
            // The controls live in their own collapsible groups, outside the character viewport.
            _animationTools.style.flexGrow=1; _targetTools.style.flexGrow=1;
            var animationOptions=new ScrollView { style={maxHeight=120} }; _animationTools.Add(animationOptions);
            var ghosts=new Toggle("前后帧"); animationOptions.Add(ghosts);
            ghosts.RegisterValueChangedCallback(e=>{_ghosts=e.newValue;Sample();});
            animationOptions.Add(_rootMotion); _rootMotion.style.display=DisplayStyle.None;
            _rootMotion.RegisterValueChangedCallback(_=>Sample());
            var sound=new Toggle("音效"); animationOptions.Add(sound);
            sound.RegisterValueChangedCallback(e=>{_sound=e.newValue;Sample();});
            animationOptions.Add(_preClip); _preClip.RegisterValueChangedCallback(_=>{_tools?.Invalidate();Sample();});
            animationOptions.Add(_sectionFrom);_sectionFrom.RegisterValueChangedCallback(_=>{_tools?.Invalidate();Sample();});
            _animationTools.RegisterValueChangedCallback(e=>{if(e.target==_animationTools)Sample();});
            _targetTools.RegisterValueChangedCallback(e=>{if(e.target==_targetTools)RefreshSelection();});
            var more=new ScrollView{style={maxHeight=120}};_targetTools.Add(more);
            var distance=new FloatField("目标距离"){value=_targetDistance};more.Add(distance);
            distance.RegisterValueChangedCallback(e=>{_targetDistance=Mathf.Max(0,e.newValue);Sample();});
            var level=new IntegerField("等级"){value=_level};more.Add(level);
            level.RegisterValueChangedCallback(e=>{_level=Mathf.Max(1,e.newValue);RefreshSelection();});
            var count=new IntegerField("假人数量"){value=_dummyCount};more.Add(count);count.RegisterValueChangedCallback(e=>{_dummyCount=Mathf.Clamp(e.newValue,1,32);Sample();});
            var layout=new EnumField("排布",_layout);more.Add(layout);layout.RegisterValueChangedCallback(e=>{_layout=(DummyLayout)e.newValue;Sample();});
            var prefab=new UnityEditor.UIElements.ObjectField("假人 Prefab"){objectType=typeof(GameObject),allowSceneObjects=false};more.Add(prefab);
            prefab.RegisterValueChangedCallback(e=>{_dummyPrefab=e.newValue as GameObject;Sample();});
            var reaction=new UnityEditor.UIElements.ObjectField("受击片段"){objectType=typeof(AnimationClip),allowSceneObjects=false};more.Add(reaction);
            reaction.RegisterValueChangedCallback(e=>{_reaction=e.newValue as AnimationClip;Sample();});
            void TagField(string label,Action<string> setter){var f=new TextField(label){isDelayed=true};more.Add(f);f.RegisterValueChangedCallback(e=>{setter(e.newValue);Sample();});}
            TagField("施法者标签",v=>_sourceTags=v);TagField("假人标签",v=>_targetTags=v);
            var targetIndex=new IntegerField("瞄准假人（0 为地面）"){value=_aimIndex};more.Add(targetIndex);targetIndex.RegisterValueChangedCallback(e=>{_aimIndex=e.newValue;Sample();});
            var sourceValue=new FloatField("条件属性样值 · 施法者");more.Add(sourceValue);sourceValue.RegisterValueChangedCallback(e=>{_sourceValue=e.newValue;Sample();});
            var targetValue=new FloatField("条件属性样值 · 假人");more.Add(targetValue);targetValue.RegisterValueChangedCallback(e=>{_targetValue=e.newValue;Sample();});
            var simulationOption=new Toggle("模拟投射物与区域");more.Add(simulationOption);simulationOption.RegisterValueChangedCallback(e=>{_simulate=e.newValue;Sample();});
            var overrides=new UnityEditor.UIElements.ObjectField("片段替换表"){objectType=typeof(SkillClipOverrideSet),allowSceneObjects=false};animationOptions.Add(overrides);
            overrides.RegisterValueChangedCallback(e=>{_override=e.newValue as SkillClipOverrideSet;_tools?.Invalidate();Sample();});
            _viewport = new IMGUIContainer(Draw) { style = { flexGrow = 1, flexBasis=0,minHeight = 35 } }; Add(_viewport);
            _caption = new Label("拖动旋转 · 滚轮缩放 · 双击聚焦"); _caption.AddToClassList("preview-caption"); Add(_caption);
            doc.Playback.Changed += Sample;
            doc.Changed += Changed; doc.Selection.Changed += RefreshSelection;
            EditorApplication.projectChanged += ProjectChanged;
        }
        private void ResolveSelection()
        {
            _range=null; _rangeNote=null;
            try { _range=SelectedActionRange.Read(_doc,_level,out _rangeOrigin,out _rangeNote); }
            catch (Exception e) { _rangeNote=e.Message; }
        }
        private void RefreshSelection()
        {
            ResolveSelection();
            if(_source!=_doc.PreviewPrefab || (_source==null && NeedsOrigin!=(_instance!=null))) Load();
            else Sample();
        }
        private void ProjectChanged() { if (_source != null) Load(); }
        private void Changed(ChangeSet change)
        {
            ResolveSelection();
            _previewSections.Clear();if(_doc.Root?.Animation()?.sections!=null)foreach(var s in _doc.Root.Animation().sections)if(s!=null)_previewSections.Add(s);
            var choices=new List<string>{"无"};foreach(var s in _previewSections)choices.Add(s.displayName+" · "+s.start.ToString("0.##")+"s");
            _sectionFrom.choices=choices;if(!choices.Contains(_sectionFrom.value))_sectionFrom.SetValueWithoutNotify("无");
            _tools?.Invalidate();
            if (_source != _doc.PreviewPrefab || (_source==null && NeedsOrigin!=(_instance!=null))) { Load(); return; }
            if (_instance != null) { _vfx?.Dispose(); _vfx = new CueVfxPreview(_instance); }
            Sample();
        }
        public void Load()
        {
            Cleanup(); _source = _doc.PreviewPrefab; _error = null;
            _preClip.SetEnabled(false);
            if (_source == null && !NeedsOrigin) { Sample(); return; }
            try
            {
                _preview = new PreviewRenderUtility();
                PreviewStage.Configure(_preview);
                if(_source!=null)_instance = _preview.InstantiatePrefabInScene(_source);
                else{_instance=new GameObject("施法原点");_preview.AddSingleGO(_instance);}
                _instance.hideFlags = HideFlags.HideAndDontSave;
                // 丢失脚本的组件以 null 出现，跳过即可，不应让整个预览失败。
                foreach (var behaviour in _instance.GetComponentsInChildren<MonoBehaviour>(true)) if (behaviour != null) behaviour.enabled = false;
                foreach (var animator in _instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                foreach (var animation in _instance.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
                _clips=Array.Empty<ClipInfo>();
                if(_source!=null){var adapter = AnimationAdapterRegistry.Resolve(_source);
                    _sampler = adapter.BeginPreview(_instance); _clips = adapter.EnumerateClips(_source);}
                var names = new List<string> { "无" }; string idle = "无";
                foreach (var clip in _clips)
                {
                    names.Add(clip.Name);
                    if (idle == "无" && (clip.Name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 || clip.Name.Contains("待机"))) idle = clip.Name;
                }
                _preClip.choices = names; _preClip.SetValueWithoutNotify(names.Contains(_preClip.value) ? _preClip.value : idle);
                _preClip.SetEnabled(_sampler is IBlendPoseSampler);
                _vfx = new CueVfxPreview(_instance);
                _tools = new SkillPreviewTools();
                _bounds = new Bounds(_instance.transform.position, Vector3.one);
                var renderers = _instance.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0) { _bounds = renderers[0].bounds; foreach (var r in renderers) _bounds.Encapsulate(r.bounds); }
                _contentBounds=_bounds; _distance = Mathf.Max(1, _bounds.size.magnitude * 1.7f);
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
            if(_instance!=null)_error=null;
            bool hasTrajectory=false;
            if(!_targetTools.value){_simulation?.Dispose();_simulation=null;_doc.PreviewEnd=0;}
            var animation = _doc.Root?.Animation();
            if (_sampler != null && animation != null)
            {
                try
                {
                    var clips = _clips;
                    string clipName=animation.ClipName; AnimationClip direct=null;
                    bool acceptsClip=_source.GetComponentInChildren<SkillAnimator>(true)?.Backend is PlayableAnimationBackend;
                    if((_override!=null && _override.TryGet(clipName,out var replacement,acceptsClip)) ||
                        (_doc.Root.ClipOverrides()!=null && _doc.Root.ClipOverrides().TryGet(clipName,out replacement,acceptsClip))){ if(!string.IsNullOrEmpty(replacement.replacement))clipName=replacement.replacement; direct=replacement.clip;}
                    AnimationClip motion=direct; GameObject motionRoot=null;
                    float natural = animation.Duration;
                    foreach (var clip in clips) if (clip.Name == clipName) { natural = clip.Length; motion=direct!=null?direct:clip.Source as AnimationClip; motionRoot=clip.SampleRoot; break; }
                    void Pose(float time)
                    {
                        float sample = time / Mathf.Max(.001f, animation.Duration) * natural;
                        if(direct!=null && _source.GetComponentInChildren<SkillAnimator>(true)?.Backend is PlayableAnimationBackend)
                        { _sampler.Sample("",0); direct.SampleAnimation(_instance.GetComponentInChildren<Animator>(true).gameObject,Mathf.Clamp01(time/animation.Duration)*direct.length); return; }
                        if(!_doc.Playback.Live && _sampler is IBlendPoseSampler sectionSampler && _sectionFrom.index>0 && _sectionFrom.index<=_previewSections.Count)
                        {
                            var from=_previewSections[_sectionFrom.index-1];
                            foreach(var to in _previewSections)
                            {
                                var jump=to.transition;float age=time-to.start;
                                if(from.end==to.start || jump.duration<=0 || age<0 || age>=jump.duration)continue;
                                float source=(from.end+(jump.mode==SkillBlendMode.Freeze?0:age))*natural/animation.Duration;
                                sectionSampler.Sample(clipName,source,clipName,sample,jump.Evaluate(age/jump.duration));return;
                            }
                        }
                        var blend = animation.BlendInSpec;
                        if (_sampler is IBlendPoseSampler mixer && !_doc.Playback.Live && time < blend.duration && _preClip.value != "无")
                            mixer.Sample(_preClip.value, blend.mode == SkillBlendMode.Freeze ? 0 : time, clipName, sample, blend.Evaluate(time / blend.duration));
                        else _sampler.Sample(clipName, sample);
                    }
                    // SampleRoot in clip metadata belongs to the source prefab. Resolve its counterpart in the preview clone.
                    Transform sampledRoot = null;
                    if(_animationTools.value && motion!=null){
                        var animator=_instance.GetComponentInChildren<Animator>(true);
                        if(direct!=null && animator!=null) sampledRoot=animator.transform;
                        else if(motionRoot!=null){
                            var path=AnimationUtility.CalculateTransformPath(motionRoot.transform,_source.transform);
                            sampledRoot=path.Length==0?_instance.transform:_instance.transform.Find(path);
                        }
                    }
                    hasTrajectory=sampledRoot!=null && _tools.HasTrajectory(motion,sampledRoot,animation.Duration,Pose,_doc.Playback.Time);
                    _tools.Rebuild(_instance,_doc,Pose,_animationTools.value && _ghosts,hasTrajectory && _rootMotion.value,DrawTargets);
                    _tools?.Audio(animation, _doc.Playback.Time, _sound && _doc.Playback.Playing, _doc.Playback.SeekVersion, _doc.Playback.Speed);
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
                catch (Exception ex) { _error = ex.Message; _tools?.Audio(null, 0, false); }
            }
            else { _vfx?.Dispose(); _tools?.Audio(null, 0, false);
                if(_doc.Root!=null && _instance!=null)
                    try{_tools.Rebuild(_instance,_doc,_=>{},false,false,DrawTargets);}catch(Exception e){_error=e.Message;}
            }
            void DrawTargets(){
                _contentBounds=_bounds;
                if(_targetTools.value){
                    _simulation??=new SkillPreviewSimulation(_preview);
                    _simulation.SourceTags=_sourceTags; _simulation.TargetTags=_targetTags; _simulation.AimIndex=_aimIndex;
                    _simulation.SourceAttribute=_sourceValue; _simulation.TargetAttribute=_targetValue; _simulation.Reaction=_reaction;
                    _simulation.Sample(_doc,_instance.transform,_dummyCount,_layout,_targetDistance,_dummyPrefab,_level,_simulate,_tools);
                    foreach(var p in _simulation.Targets)_contentBounds.Encapsulate(_instance.transform.TransformPoint(p));
                    _doc.PreviewEnd=_simulate?_simulation.End:0;
                }
                if(_range is TargetShape shape){
                    var source=_instance.transform;
                    var target=_simulation!=null && _aimIndex>0 && _simulation.Targets.Count>0
                        ? _simulation.Targets[Mathf.Min(_aimIndex,_simulation.Targets.Count)-1] : Vector3.forward*_targetDistance;
                    var center=_rangeOrigin==SkillSpawnPosition.Target || _rangeOrigin==SkillSpawnPosition.TargetPoint ? target : Vector3.zero;
                    var point=source.TransformPoint(center); _tools.Query(shape,source,point);
                    float extent=Mathf.Max(shape.Radius,shape.HalfExtents.magnitude);
                    _contentBounds.Encapsulate(new Bounds(point,Vector3.one*(2*extent)));

                }
            }
            _rootMotion.style.display=hasTrajectory?DisplayStyle.Flex:DisplayStyle.None;
            _caption.text = _error ?? _cueWarning ?? _rangeNote ?? (_simulation!=null?$"几何预演 · 命中 {_simulation.HitCount} / {_dummyCount}（不含阵营与物理遮挡）":"拖动旋转 · 滚轮缩放 · 双击聚焦");
            _caption.tooltip = _cueWarning;
            _viewport.MarkDirtyRepaint();
        }
        private void Draw()
        {
            Rect rect = GUILayoutUtility.GetRect(10, 10000, 35, 10000);
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
            _tools?.Draw(_preview);
            var rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0);
            var frame=_contentBounds; float zoom=_distance;
            if(_range.HasValue || _simulation!=null){
                float halfFov=_preview.camera.fieldOfView*.5f*Mathf.Deg2Rad;
                float fit=Mathf.Atan(Mathf.Tan(halfFov)*Mathf.Min(1,rect.width/rect.height));
                float userZoom=_distance/Mathf.Max(1,_bounds.size.magnitude*1.7f);
                zoom=Mathf.Max(1,frame.extents.magnitude*1.05f/Mathf.Sin(fit))*userZoom;
            }
            _preview.camera.transform.position = frame.center + rotation * new Vector3(0, 0, -zoom);
            _preview.camera.transform.rotation = rotation;
            _preview.camera.nearClipPlane = .01f; _preview.camera.farClipPlane = 200;
            _preview.Render(true);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
            string caption = _error ?? _cueWarning ?? (_doc.Root?.Animation()?.ClipName ?? "角色预览") + $"  @ {_doc.Playback.Time:0.00}s";
            GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, 22), caption, EditorStyles.whiteMiniLabel);
            var animationAsset = _doc.Root?.Animation();
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
            _simulation?.Dispose(); _simulation=null; _doc.PreviewEnd=0;
            _tools?.Dispose(); _tools = null;
            _vfx?.Dispose(); _vfx = null;
            foreach (var cue in _cues) { try { cue.Dispose(); } catch (Exception e) { Debug.LogException(e); } } _cues.Clear();
            try { _sampler?.Dispose(); } finally { _sampler = null; _preview?.Cleanup(); _preview = null; _instance = null; }
        }
        public void Dispose() { _doc.Playback.Changed -= Sample; _doc.Changed -= Changed; _doc.Selection.Changed -= RefreshSelection; EditorApplication.projectChanged -= ProjectChanged; Cleanup(); }
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
