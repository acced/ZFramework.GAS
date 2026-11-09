using System;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>Write a timeline from a project importer or template without depending on its serialized layout.</summary>
    public sealed class SkillTimelineAuthoring
    {
        private readonly SerializedObject _asset;
        private SkillTimelineAuthoring(SerializedObject asset) => _asset = asset;

        // Apply the batch only after the caller finishes. Saving to disk is the caller's decision.
        public static void Edit(SkillAnimationAsset asset, Action<SkillTimelineAuthoring> write, bool recordUndo = false)
        {
            using var so = new SerializedObject(asset);
            write(new SkillTimelineAuthoring(so));
            if (recordUndo) so.ApplyModifiedProperties(); else so.ApplyModifiedPropertiesWithoutUndo();
        }

        public void Clip(AnimationClip clip, float duration, float frameRate = 30, float blendIn = .12f, float blendOut = .18f)
        {
            if (!float.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
            if (!float.IsFinite(frameRate) || frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
            if (!float.IsFinite(blendIn) || blendIn < 0 || blendIn > duration) throw new ArgumentOutOfRangeException(nameof(blendIn));
            if (!float.IsFinite(blendOut) || blendOut < 0) throw new ArgumentOutOfRangeException(nameof(blendOut));
            _asset.FindProperty("clip").objectReferenceValue = clip;
            _asset.FindProperty("duration").floatValue = duration;
            _asset.FindProperty("frameRate").floatValue = frameRate;
            _asset.FindProperty("blendIn").floatValue = blendIn;
            _asset.FindProperty("blendOut").floatValue = blendOut;
        }

        public void Marker(string id, float time, string label, params SkillAction[] actions)
        {
            var item = Element(Events, id, time, time);
            item.FindPropertyRelative("key").stringValue = label;
            var list = item.FindPropertyRelative("actions");
            list.ClearArray();
            list.arraySize = actions.Length;
            for (int i = 0; i < actions.Length; i++) list.GetArrayElementAtIndex(i).managedReferenceValue = actions[i];
        }

        public void Cue(string id, float start, float end, string tag) =>
            Element(Cues, id, start, end).FindPropertyRelative("cueTag.m_Name").stringValue = tag;

        public void TagWindow(string id, float start, float end, params string[] tags)
        {
            var list = Element(Windows, id, start, end).FindPropertyRelative("tags");
            list.arraySize = tags.Length;
            for (int i = 0; i < tags.Length; i++) list.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue = tags[i];
        }

        private static readonly EventTrack Events = new();
        private static readonly CueRegionTrack Cues = new();
        private static readonly TagWindowTrack Windows = new();

        private SerializedProperty Element(ITrackType track, string id, float start, float end)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("导入的时间轴元素需要稳定 ID。", nameof(id));
            if (!float.IsFinite(start) || start < 0 || !float.IsFinite(end) || end >= 0 && end < start)
                throw new ArgumentOutOfRangeException(nameof(start), "时间范围无效。");
            var item = TimelineCommands.Find(_asset, track.PropertyPath, id);
            if (item == null)
            {
                foreach (var other in TrackTypeRegistry.Types)
                    if (other.PropertyPath != track.PropertyPath && TimelineCommands.Find(_asset, other.PropertyPath, id) != null)
                        throw new ArgumentException("时间轴 ID 已由其他轨道使用：" + id, nameof(id));
                var list = _asset.FindProperty(track.PropertyPath);
                item = list.GetArrayElementAtIndex(list.arraySize++);
                item.FindPropertyRelative("id").stringValue = id;
            }
            track.WriteSpan(item, start, end);
            return item;
        }
    }
}
