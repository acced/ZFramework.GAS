using System.Collections.Generic;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class SpanRule : ISkillValidationRule
    {
        public string Id => "GAS011";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation) return;
            if (!(animation.Duration > 0) || !float.IsFinite(animation.Duration)) { c.Add(issues, Id, asset, "duration", "动画时长必须是有限正数"); return; }
            if (!(animation.FrameRate > 0) || !float.IsFinite(animation.FrameRate)) c.Add(issues, Id, asset, "frameRate", "显示帧率必须是有限正数");
            var so = c.Document.Serialized(asset);
            foreach (var track in TrackTypeRegistry.Types)
            {
                var array = so.FindProperty(track.PropertyPath); if (array == null) continue;
                bool endAtTimeline = track is CueRegionTrack || track is TagWindowTrack;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var e = array.GetArrayElementAtIndex(i); var span = track.ReadSpan(e); string id = e.FindPropertyRelative("id").stringValue;
                    if (!float.IsFinite(span.start) || !float.IsFinite(span.end) || span.start < 0 || span.start > animation.Duration || span.end > animation.Duration || span.end < 0 && !endAtTimeline || span.end >= 0 && span.end < span.start)
                        c.Add(issues, Id, asset, e.propertyPath, track.DisplayName + "超出时长或起止顺序错误", fix: c.Edit(asset, "修复时间范围", target =>
                        {
                            var p = TimelineCommands.Find(target, track.PropertyPath, id); if (p == null) return; var s = track.ReadSpan(p);
                            float start = float.IsFinite(s.start) ? Mathf.Clamp(s.start, 0, animation.Duration) : 0;
                            float end = float.IsFinite(s.end) ? s.end < 0 && endAtTimeline ? -1 : Mathf.Clamp(s.end, start, animation.Duration) : animation.Duration;
                            track.WriteSpan(p, start, end);
                        }), track: track.PropertyPath, elementId: id);
                }
            }
        }
    }
    public sealed class BlendRule : ISkillValidationRule
    {
        public string Id => "GAS014";
        public void Validate(ValidationContext c, ScriptableObject asset, List<Issue> issues)
        {
            if (asset is not SkillAnimationAsset animation) return;
            if (!float.IsFinite(animation.BlendIn) || animation.BlendIn < 0 || animation.BlendIn > animation.Duration)
                c.Add(issues, Id, asset, "blendIn", "混入时长须在 0 到时间轴时长之间。",
                    fix: c.Edit(asset, "修复混入时长", so => so.FindProperty("blendIn").floatValue =
                        float.IsFinite(animation.BlendIn) ? Mathf.Clamp(animation.BlendIn, 0, animation.Duration) : 0));
            if (!float.IsFinite(animation.BlendOut) || animation.BlendOut < 0)
                c.Add(issues, Id, asset, "blendOut", "混出时长须为有限非负数。",
                    fix: c.Edit(asset, "修复混出时长", so => so.FindProperty("blendOut").floatValue = 0));
        }
    }
}
