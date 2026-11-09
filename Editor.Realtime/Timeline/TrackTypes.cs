using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public enum TrackElementShape { Point, Range }
    public interface ITrackType
    {
        string DisplayName { get; }
        Color Color { get; }
        string PropertyPath { get; }
        TrackElementShape Shape { get; }
        (float start, float end) ReadSpan(SerializedProperty element);
        void WriteSpan(SerializedProperty element, float start, float end);
        string Label(SerializedProperty element);
        void InitializeNew(SerializedProperty element, float time, Object dropped);
        bool AcceptsDrop(Object dropped);
    }
    public abstract class TrackType : ITrackType
    {
        public abstract string DisplayName { get; }
        public abstract Color Color { get; }
        public abstract string PropertyPath { get; }
        public virtual TrackElementShape Shape => TrackElementShape.Point;
        public virtual (float start, float end) ReadSpan(SerializedProperty e) => Shape == TrackElementShape.Point
            ? (e.FindPropertyRelative("time").floatValue, e.FindPropertyRelative("time").floatValue)
            : (e.FindPropertyRelative("start").floatValue, e.FindPropertyRelative("end").floatValue);
        public virtual void WriteSpan(SerializedProperty e, float start, float end)
        {
            if (!float.IsFinite(start) || !float.IsFinite(end)) throw new ArgumentOutOfRangeException(nameof(start), "时间必须为有限数。");
            e.FindPropertyRelative(Shape == TrackElementShape.Point ? "time" : "start").floatValue = start;
            if (Shape == TrackElementShape.Range) e.FindPropertyRelative("end").floatValue = end;
        }
        public abstract string Label(SerializedProperty element);
        public virtual bool AcceptsDrop(Object dropped) => false;
        public virtual void InitializeNew(SerializedProperty element, float time, Object dropped)
        { element.FindPropertyRelative("id").stringValue = Guid.NewGuid().ToString("N"); WriteSpan(element, time, -1); }
    }
    public sealed class EventTrack : TrackType
    {
        public override string DisplayName => "事件 · 效果";
        public override Color Color => SkillColors.Ability;
        public override string PropertyPath => "markers";
        public override string Label(SerializedProperty e)
        {
            string key = e.FindPropertyRelative("key").stringValue;
            var actions = e.FindPropertyRelative("actions");
            return (string.IsNullOrEmpty(key) ? "事件" : key) + (actions.arraySize > 0 ? " · " + actions.arraySize + " 动作" : "");
        }
        public override bool AcceptsDrop(Object dropped) => dropped is GameplayEffectAsset;
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        {
            base.InitializeNew(e, time, dropped);
            e.FindPropertyRelative("key").stringValue = "";
            var actions=e.FindPropertyRelative("actions");actions.ClearArray();
            if(dropped is GameplayEffectAsset effect){actions.arraySize=1;actions.GetArrayElementAtIndex(0).managedReferenceValue=new ApplyEffectAction{effect=effect};}
        }
    }
    public sealed class CueRegionTrack : TrackType
    {
        public override string DisplayName => "Cue 区间";
        public override Color Color => SkillColors.Cue;
        public override string PropertyPath => "cueRegions";
        public override TrackElementShape Shape => TrackElementShape.Range;
        public override string Label(SerializedProperty e) => e.FindPropertyRelative("cueTag.m_Name").stringValue;
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        { base.InitializeNew(e, time, dropped); e.FindPropertyRelative("cueTag.m_Name").stringValue = ""; }
    }
    public sealed class TagWindowTrack : TrackType
    {
        public override string DisplayName => "标签窗口";
        public override Color Color => SkillColors.Infinite;
        public override string PropertyPath => "tagWindows";
        public override TrackElementShape Shape => TrackElementShape.Range;
        public override string Label(SerializedProperty e)
        {
            var tags = e.FindPropertyRelative("tags");
            return tags.arraySize == 0 ? "标签窗口" : tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue + (tags.arraySize > 1 ? " +" + (tags.arraySize - 1) : "");
        }
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        { base.InitializeNew(e, time, dropped); e.FindPropertyRelative("tags").ClearArray(); }
    }
    public sealed class SectionTrack : TrackType
    {
        public override string DisplayName => "动画段";
        public override Color Color => SkillColors.Animation;
        public override string PropertyPath => "sections";
        public override TrackElementShape Shape => TrackElementShape.Range;
        public override string Label(SerializedProperty e) => e.FindPropertyRelative("displayName").stringValue;
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        {
            base.InitializeNew(e, time, dropped);
            e.FindPropertyRelative("displayName").stringValue = "新动画段";
            e.FindPropertyRelative("end").floatValue = time + .5f;
            e.FindPropertyRelative("nextSectionId").stringValue = "";
            e.FindPropertyRelative("inputBufferStart").floatValue = time;
            e.FindPropertyRelative("inputBufferEnd").floatValue = time + .5f;
        }
    }
    public sealed class HitWindowTrack : TrackType
    {
        public override string DisplayName => "命中窗口";
        public override string PropertyPath => "hitWindows";
        public override Color Color => SkillColors.Instant;
        public override TrackElementShape Shape => TrackElementShape.Range;
        internal static string Span(SerializedProperty e) =>
            $"{e.FindPropertyRelative("start").floatValue:0.00#}–{e.FindPropertyRelative("end").floatValue:0.00#} s";
        public override string Label(SerializedProperty e) =>
            Span(e) + " · " + (e.FindPropertyRelative("effect").objectReferenceValue?.name ?? "选择命中效果") + " · " + SourceLabel((TargetQuery)e.FindPropertyRelative("targets").boxedValue);
        internal static string SourceLabel(TargetQuery q) => q.kind switch {
            TargetQueryKind.Hitbox => "碰撞体：" + q.sourceKey, TargetQueryKind.Project => "由项目提供：" + q.sourceKey,
            TargetQueryKind.FixedTarget => "指定对象", TargetQueryKind.Self => "自身", TargetQueryKind.Circle => "圆形范围",
            TargetQueryKind.TargetPoint => "目标点周围", TargetQueryKind.Sector => "扇形范围", _ => "前方长方体" };
        public override bool AcceptsDrop(Object dropped) => dropped is GameplayEffectAsset;
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        {
            float duration = ((SkillAnimationAsset)e.serializedObject.targetObject).Duration;
            float start = Mathf.Clamp(time, 0, Mathf.Max(0, duration - .01f));
            e.boxedValue = new SkillHitWindow { start = start, end = Mathf.Min(duration, start + .25f), effect = dropped as GameplayEffectAsset };
        }
    }
    public sealed class RuntimeTrack : TrackType
    {
        public override string DisplayName => "曲线 / 扩展轨道";
        public override Color Color => SkillColors.Task;
        public override string PropertyPath => "runtimeTracks";
        public override TrackElementShape Shape => TrackElementShape.Range;
        public override string Label(SerializedProperty element) => element.managedReferenceValue switch {
            CurveSkillTrack curve=>string.IsNullOrEmpty(curve.channelId)?"曲线":curve.channelId, CameraShakeTrack=>"震屏",
            HitStopTrack=>"顿帧",TimeScaleTrack=>"全局变速",LookAtTrack=>"瞄准",null=>"未配置",var track=>track.GetType().Name };
        public override void InitializeNew(SerializedProperty element, float time, Object dropped)
        { element.managedReferenceValue = new CurveSkillTrack { start = time, end = time + .25f }; base.InitializeNew(element, time, dropped); element.FindPropertyRelative("end").floatValue = time + .25f; }
    }
    public static class TrackTypeRegistry
    {
        public static readonly IReadOnlyList<ITrackType> Types = TypeCache.GetTypesDerivedFrom<ITrackType>()
            .Where(t => !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t == typeof(EventTrack) ? 0 : t == typeof(CueRegionTrack) ? 1 : t == typeof(TagWindowTrack) ? 2 : 3)
            .Select(t => (ITrackType)Activator.CreateInstance(t)).ToArray();
        public static ITrackType Get(string path) => Types.FirstOrDefault(t => t.PropertyPath == path);
    }
    public static class TimelineMath
    {
        private static readonly float[] RulerSteps = { .05f, .1f, .25f, .5f, 1, 2, 5 };
        public static float RulerStep(float pixelsPerSecond)
        {
            float minimum = 56 / Mathf.Max(1, pixelsPerSecond);
            foreach (float step in RulerSteps) if (step >= minimum) return step;
            float scale = 10;
            while (scale < minimum) scale *= 10;
            return scale;
        }
        public static float Snap(float time, float fps, bool bypass = false) => bypass || fps <= 0 ? time : Mathf.Round(time * fps) / fps;
        public static float X(float time, float pixelsPerSecond, float offset) => time * pixelsPerSecond - offset;
        public static float Time(float x, float pixelsPerSecond, float offset) => (x + offset) / pixelsPerSecond;
    }
}
