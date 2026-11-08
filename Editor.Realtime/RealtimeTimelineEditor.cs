using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    internal sealed class RealtimeTimelineEditor : ISkillTimelineEditor
    {
        [InitializeOnLoadMethod]
        private static void Register() => SkillEditorExtensions.RegisterTimeline(new RealtimeTimelineEditor());

        public Type AssetType => typeof(SkillAnimationAsset);
        public string DisplayName => "动画";
        public string EntryTrack => "markers";
        public SkillTemplate DefaultTemplate => new MeleeTemplate();
        public IEnumerable<Type> ManagedReferenceRoots => new[] { typeof(AnimationBackend), typeof(SkillRuntimeTrack) };
        public ISkillTimelineWorkspace OpenWorkspace(SkillDocument doc) => new RealtimeWorkspace(doc);
        public RuntimeSnapshot.PresentationTask Capture(GameplayAbilityTask task) =>
            task is AnimationTask animation ? new AnimationTaskSnapshot(animation) : null;

        public ScriptableObject CreateEmbedded(GameplayAbilityAsset root, string name)
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(root, name);
            using var so = new SerializedObject(animation);
            so.FindProperty("clipName").stringValue = "Attack";
            so.ApplyModifiedProperties();
            return animation;
        }
        public void EnsureIds(ScriptableObject asset, bool recordUndo) => TimelineCommands.EnsureIds((SkillAnimationAsset)asset, recordUndo);
        public void BuildDefinition(ScriptableObject asset) => _ = ((SkillAnimationAsset)asset).Definition;
        public float Duration(ScriptableObject asset) => ((SkillAnimationAsset)asset).Duration;
        public string AttachmentError(ScriptableObject asset)
        {
            var animation = (SkillAnimationAsset)asset;
            return string.IsNullOrWhiteSpace(animation.ClipName) || !(animation.Duration > 0) || !float.IsFinite(animation.Duration)
                ? "动画需要片段名和有效的正时长" : null;
        }
        public string TrackLabel(string track) => TrackTypeRegistry.Get(track)?.DisplayName;
        public IEnumerable<SkillFlowEntry> Entries(SerializedObject asset)
        {
            var markers = asset.FindProperty(EntryTrack);
            for (int i = 0; i < markers.arraySize; i++)
            {
                var marker = markers.GetArrayElementAtIndex(i);
                float time = marker.FindPropertyRelative("time").floatValue;
                string label = $"标记 {marker.FindPropertyRelative("key").stringValue} · {time:0.###}s";
                yield return new SkillFlowEntry(marker.FindPropertyRelative("id").stringValue, label, marker.propertyPath, marker.FindPropertyRelative("actions"), time);
            }
        }
        public IEnumerable<SkillNodeCue> Cues(SerializedObject asset)
        {
            var regions = asset.FindProperty("cueRegions");
            for (int i = 0; i < regions.arraySize; i++)
            {
                var region = regions.GetArrayElementAtIndex(i);
                float end = region.FindPropertyRelative("end").floatValue;
                string span = $"{region.FindPropertyRelative("start").floatValue:0.##}–" + (end < 0 ? "动画结束" : $"{end:0.##}s");
                yield return new SkillNodeCue(region.FindPropertyRelative("cueTag.m_Name").stringValue, "cueRegions",
                    region.FindPropertyRelative("id").stringValue, span);
            }
        }
        public void AddNodeFields(ISkillNodeFields node, SerializedObject asset)
        {
            node.Field(asset, "clipName", new TextField("片段") { isDelayed = true });
            node.Field(asset, "duration", new FloatField("时长 / s") { isDelayed = true });
            node.Field(asset, "loop", new Toggle("循环"));
        }
    }
}
