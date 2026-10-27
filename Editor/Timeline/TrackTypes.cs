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
            var effect = e.FindPropertyRelative("effect").objectReferenceValue;
            return (string.IsNullOrEmpty(key) ? "事件" : key) + (effect != null ? " → " + effect.name : "");
        }
        public override bool AcceptsDrop(Object dropped) => dropped is GameplayEffectAsset;
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        {
            base.InitializeNew(e, time, dropped);
            e.FindPropertyRelative("key").stringValue = "";
            e.FindPropertyRelative("effect").objectReferenceValue = dropped as GameplayEffectAsset;
            e.FindPropertyRelative("target").enumValueIndex = 0;
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
    public sealed class TaskTrack : TrackType
    {
        public override string DisplayName => "任务";
        public override Color Color => SkillColors.Task;
        public override string PropertyPath => "taskEvents";
        public override string Label(SerializedProperty e) => e.FindPropertyRelative("spawner").managedReferenceValue?.GetType().Name ?? "选择任务类型";
        public override void InitializeNew(SerializedProperty e, float time, Object dropped)
        { base.InitializeNew(e, time, dropped); e.FindPropertyRelative("spawner").managedReferenceValue = new ProjectileSpawner(); }
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
        public static float Snap(float time, float fps, bool bypass = false) => bypass || fps <= 0 ? time : Mathf.Round(time * fps) / fps;
        public static float X(float time, float pixelsPerSecond, float offset) => time * pixelsPerSecond - offset;
        public static float Time(float x, float pixelsPerSecond, float offset) => (x + offset) / pixelsPerSecond;
    }
}
