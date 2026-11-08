using System;
using System.Collections.Generic;
using System.Linq;
using GameplayTags;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class ClipRule : ISkillValidationRule
    {
        public string Id => "GAS010";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation) return;
            if (string.IsNullOrWhiteSpace(animation.ClipName)) c.Add(issues, Id, asset, "clipName", "动画片段名为空");
            else if (c.Document.PreviewPrefab != null)
            {
                var adapter = AnimationAdapterRegistry.Resolve(c.Document.PreviewPrefab);
                if (!adapter.EnumerateClips(c.Document.PreviewPrefab).Any(x => x.Name == animation.ClipName))
                    c.Add(issues, Id, asset, "clipName", "预览角色上找不到片段「" + animation.ClipName + "」", IssueSeverity.Warning);
            }
        }
    }
    public sealed class SpanRule : ISkillValidationRule
    {
        public string Id => "GAS011";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation) return;
            bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
            if (!(animation.Duration > 0) || !Finite(animation.Duration)) { c.Add(issues, Id, asset, "duration", "动画时长必须是有限正数"); return; }
            if (!(animation.FrameRate > 0) || !Finite(animation.FrameRate)) c.Add(issues, Id, asset, "frameRate", "显示帧率必须是有限正数");
            var so = c.Document.Serialized(asset);
            foreach (var track in TrackTypeRegistry.Types)
            {
                var array = so.FindProperty(track.PropertyPath); if (array == null) continue;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var e = array.GetArrayElementAtIndex(i); var span = track.ReadSpan(e); string id = e.FindPropertyRelative("id").stringValue;
                    if (!Finite(span.start) || !Finite(span.end) || span.start < 0 || span.start > animation.Duration || span.end > animation.Duration || span.end >= 0 && span.end < span.start)
                        c.Add(issues, Id, asset, e.propertyPath, track.DisplayName + "超出时长或起止顺序错误", fix: c.Edit(asset, "修复时间范围", target =>
                        {
                            var p = TimelineCommands.Find(target, track.PropertyPath, id); if (p == null) return; var s = track.ReadSpan(p);
                            float start = Finite(s.start) ? Mathf.Clamp(s.start, 0, animation.Duration) : 0;
                            float end = Finite(s.end) ? s.end < 0 ? -1 : Mathf.Clamp(s.end, start, animation.Duration) : animation.Duration;
                            track.WriteSpan(p, start, end);
                        }), track: track.PropertyPath, elementId: id);
                }
            }
        }
    }
    public sealed class SectionEndRule : ISkillValidationRule
    {
        public string Id => "GAS_SECTION_END";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation || animation.sections == null) return;
            var starts = new HashSet<float>();
            foreach (var section in animation.sections) if (section != null) starts.Add(section.start);
            var so = c.Document.Serialized(animation);
            foreach (string track in new[] { "markers" })
            {
                var events = so.FindProperty(track);
                for (int i = 0; i < events.arraySize; i++)
                {
                    var item = events.GetArrayElementAtIndex(i); float time = item.FindPropertyRelative("time").floatValue;
                    // Exact authored starts belong to the destination. A nearby (but distinct)
                    // time can still collapse onto an exclusive end when the clip is stretched.
                    if (starts.Contains(time)) continue;
                    foreach (var section in animation.sections)
                        if (section != null && Math.Abs(time - section.end) <= Math.Max(float.Epsilon, Math.Abs(section.end) * 1.1920929e-7f))
                        {
                            c.Add(issues, Id, asset, item.propertyPath, "事件接近 Section「" + section.displayName + "」的排他终点，且不在任何段的起点。缩放舍入、结束、循环或跳段可能跳过它。属于本段的事件请提早；属于下一段的事件请对齐其起点。",
                                IssueSeverity.Warning, track: track, elementId: item.FindPropertyRelative("id").stringValue);
                            break;
                        }
                }
            }
        }
    }
    public sealed class CueOverlapRule : ISkillValidationRule
    {
        public string Id => "GAS015";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation || !(animation.Duration > 0)) return;
            var array = c.Document.Serialized(asset).FindProperty("cueRegions");
            var seen = new Dictionary<string, List<(float start, float end)>>();
            for (int i = 0; i < array.arraySize; i++)
            {
                var item = array.GetArrayElementAtIndex(i);
                string tag = item.FindPropertyRelative("cueTag.m_Name").stringValue;
                float start = item.FindPropertyRelative("start").floatValue, end = item.FindPropertyRelative("end").floatValue;
                if (end < 0) end = animation.Duration;
                if (string.IsNullOrEmpty(tag) || !(start >= 0 && end > start && end <= animation.Duration)) continue;
                if (!seen.TryGetValue(tag, out var spans)) seen[tag] = spans = new();
                if (spans.Any(s => start < s.end && s.start < end))
                    c.Add(issues, Id, asset, item.propertyPath, $"Cue「{tag}」存在重叠区间，请确认是否有意重复触发。", IssueSeverity.Warning,
                        track: "cueRegions", elementId: item.FindPropertyRelative("id").stringValue);
                spans.Add((start, end));
            }
        }
    }
    public sealed class BlendRule : ISkillValidationRule
    {
        public string Id => "GAS014";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation || !(animation.Duration > 0)) return;
            // 混入超过动画时长时运行期定义会直接拒绝；混出只需非负。
            var backend = c.Document.PreviewPrefab?.GetComponentInChildren<SkillAnimator>(true)?.Backend;
            // Buffered input jumps only at the source section's END. Adjacent sections
            // continue without SeekBlend; a window that opens early is not by itself a risk.
            var early=new HashSet<AnimationSection>();
            var sections=animation.sections;
            if(backend is AnimatorBackend){
                if(animation.BlendIn>0)c.Add(issues,Id,asset,"blendIn","Animator：若打断后在混入期内重放同一技能状态，将直接从入口重播。",IssueSeverity.Info,track:"@field",elementId:"blendIn");
                if(sections?.Length>0 && sections[0]!=null){
                    // Track time remaining in the current blend, not absolute clip time.
                    // Non-adjacent entry starts a new transition; walk chains and loops
                    // only while they can improve the remaining time at that section.
                    var pending=new Queue<(AnimationSection section,float remaining)>();
                    var visited=new HashSet<(AnimationSection,float)>();
                    pending.Enqueue((sections[0],animation.BlendIn));
                    while(pending.Count>0){
                        var (from,remaining)=pending.Dequeue();
                        if(!visited.Add((from,remaining)) || from.end<=from.start)continue;
                        float left=remaining-(from.end-from.start);
                        bool buffered=Math.Max(from.start,from.inputBufferStart)<=Math.Min(from.end,from.inputBufferEnd);
                        foreach(var to in sections)if(to!=null && (buffered || from.nextSectionId==to.Id)){
                            bool continuous=to.start==from.end;
                            if(!continuous && left>0)early.Add(to);
                            // An interrupted Mecanim transition seeks directly, so it starts no new blend.
                            float next=continuous?Math.Max(0,left):left>0?0:Math.Max(0,to.transition.duration);
                            pending.Enqueue((to,next));
                        }
                    }
                }
            }
            void Check(string path, SkillBlend spec, bool section,bool earlyInput=false)
            {
                if (spec.duration <= 0 && !earlyInput) return;
                if (spec.curve == SkillBlendCurve.Custom && (spec.custom == null || spec.custom.length < 2))
                    c.Add(issues, Id, asset, path, "自定义过渡曲线至少需要两个键。", track: "@field", elementId: path);
                if (backend == null) return;
                var caps = backend.BlendCapabilities;
                var missing = new List<string>();
                if(earlyInput)
                    missing.Add("混入尚未结束就输入连招并跳到同一状态时，Animator 会直接定位；需要连续姿势可使用 Playables");
                if (section && (caps & AnimationBlendCapabilities.SeekBlend) == 0) missing.Add("跳段将直接定位");
                if (spec.curve != SkillBlendCurve.Linear && (caps & AnimationBlendCapabilities.Curve) == 0) missing.Add("曲线退回后端默认过渡");
                if (spec.mode == SkillBlendMode.Freeze && (caps & AnimationBlendCapabilities.Freeze) == 0 ||
                    !Enum.IsDefined(typeof(SkillBlendMode),spec.mode)) missing.Add("模式退回普通过渡");
                if (missing.Count > 0) c.Add(issues, Id, asset, path, backend.BackendId + "：" + string.Join("；", missing), IssueSeverity.Warning, track: "@field", elementId: path);
            }
            Check("blendInCurve", animation.BlendInSpec, false); Check("blendOutCurve", animation.BlendOutSpec, false);
            if (animation.sections != null) for (int i=0; i<animation.sections.Length; i++)
            {
                var s = animation.sections[i]; if (s == null) continue; string path = $"sections.Array.data[{i}].transition";
                if (!(s.transition.duration >= 0 && s.transition.duration <= s.end-s.start)) c.Add(issues, Id, asset, path, "跳入过渡须在 0 与段长之间。", track:"@field", elementId:path);
                Check(path, s.transition, true,early.Contains(s));
            }
            if (backend != null && backend is not PlayableAnimationBackend && (animation.mask != null || animation.boneWeights!=null || animation.additive || animation.layerWeight != 1))
                c.Add(issues, Id, asset, "mask", backend.BackendId + " 不使用技能遮罩或层权重。", IssueSeverity.Warning, track:"@field", elementId:"mask");
            if (!(animation.BlendIn >= 0) || animation.BlendIn > animation.Duration)
                c.Add(issues, Id, asset, "blendIn", $"混入时长 {animation.BlendIn:0.###}s 须在 0 到动画时长 {animation.Duration:0.###}s 之间",
                    fix: c.Edit(asset, "修复混入时长", so => so.FindProperty("blendIn").floatValue = Mathf.Clamp(so.FindProperty("blendIn").floatValue, 0, animation.Duration)));
            if (!(animation.BlendOut >= 0) || float.IsInfinity(animation.BlendOut))
                c.Add(issues, Id, asset, "blendOut", "混出时长须为非负秒数",
                    fix: c.Edit(asset, "修复混出时长", so => so.FindProperty("blendOut").floatValue = 0));
        }
    }
    public sealed class MarkerKeyRule : ISkillValidationRule
    {
        public string Id => "GAS012";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset) return;
            var array = c.Document.Serialized(asset).FindProperty("markers"); var seen = new HashSet<string>();
            for (int i = 0; i < array.arraySize; i++)
            {
                var e = array.GetArrayElementAtIndex(i); string key = e.FindPropertyRelative("key").stringValue, id = e.FindPropertyRelative("id").stringValue;
                if (!string.IsNullOrEmpty(key) && !seen.Add(key))
                    c.Add(issues, Id, asset, e.propertyPath, "事件名称重复：「" + key + "」", fix: c.Edit(asset, "修复事件名称", so =>
                    {
                        var p = TimelineCommands.Find(so, "markers", id); if (p == null) return;
                        var names = new HashSet<string>(); var all = so.FindProperty("markers");
                        for (int j = 0; j < all.arraySize; j++) names.Add(all.GetArrayElementAtIndex(j).FindPropertyRelative("key").stringValue);
                        int suffix = 2; while (names.Contains(key + "_" + suffix)) suffix++; p.FindPropertyRelative("key").stringValue = key + "_" + suffix;
                    }), track: "markers", elementId: id);
            }
        }
    }
    public sealed class ClipLengthRule : ISkillValidationRule
    {
        public string Id => "GAS013";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation || c.Document.PreviewPrefab == null) return;
            var clips = AnimationAdapterRegistry.Resolve(c.Document.PreviewPrefab).EnumerateClips(c.Document.PreviewPrefab);
            foreach (var clip in clips)
                if (clip.Name == animation.ClipName && Mathf.Abs(clip.Length - animation.Duration) > .5f / Mathf.Max(1, animation.FrameRate))
                    c.Add(issues, Id, asset, "duration", $"授权时长 {animation.Duration:0.###}s 与片段 {clip.Length:0.###}s 不同", IssueSeverity.Warning);
        }
    }
    public sealed class BackendRule : ISkillValidationRule
    {
        public string Id => "GAS031";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset != c.Document.Root || c.Document.PreviewPrefab == null) return;
            var animator = c.Document.PreviewPrefab.GetComponentInChildren<SkillAnimator>(true);
            if (animator == null) c.Add(issues, Id, asset, "", "预览角色未挂 SkillAnimator；宿主仍可手动创建驱动", IssueSeverity.Warning);
            else if (animator.Backend == null) c.Add(issues, Id, asset, "", "SkillAnimator 未选择后端", IssueSeverity.Warning);
            else if (!animator.Backend.Validate(c.Document.PreviewPrefab, out string error)) c.Add(issues, Id, asset, "", error, IssueSeverity.Warning);
            else if (animator.Backend is AnimatorBackend backend)
            {
                var controller = c.Document.PreviewPrefab.GetComponentInChildren<Animator>(true)?.runtimeAnimatorController;
                while (controller is AnimatorOverrideController overrides) controller = overrides.runtimeAnimatorController;
                if (controller is UnityEditor.Animations.AnimatorController ac && backend.layer >= ac.layers.Length)
                    c.Add(issues, Id, asset, "", "Animator 层索引越界", IssueSeverity.Warning);
            }
        }
    }
}
