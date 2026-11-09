using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    // One optional timeline workspace per editor domain; one set of panels per window.
    public static class SkillEditorExtensions
    {
        public static ISkillTimelineEditor Timeline { get; private set; }
        public static void RegisterTimeline(ISkillTimelineEditor editor)
        {
            if (editor == null) throw new ArgumentNullException(nameof(editor));
            if (Timeline != null && Timeline != editor) throw new InvalidOperationException("A timeline editor is already registered.");
            Timeline = editor;
        }

        internal static bool Supports(Object asset) => asset != null && Timeline?.AssetType.IsInstanceOfType(asset) == true;
        internal static ScriptableObject Asset(GameplayAbilityAsset ability) =>
            ability != null && Supports(ability.Presentation) ? ability.Presentation : null;
        internal static void EnsureIds(Object asset, bool undo = false)
        {
            if (Supports(asset)) Timeline.EnsureIds((ScriptableObject)asset, undo);
        }
        internal static IEnumerable<SkillFlowEntry> Entries(SerializedObject asset) =>
            asset != null && Supports(asset.targetObject) ? Timeline.Entries(asset) : Array.Empty<SkillFlowEntry>();
        internal static IEnumerable<SkillTimelineEffectSlot> EffectSlots(SerializedObject asset) =>
            Supports(asset.targetObject) && Timeline is ISkillTimelineEffectSlots slots ? slots.EffectSlots(asset) : Array.Empty<SkillTimelineEffectSlot>();
    }

    public interface ISkillTimelineEditor
    {
        Type AssetType { get; }
        string DisplayName { get; }
        string EntryTrack { get; }
        IEnumerable<Type> ManagedReferenceRoots { get; }
        ScriptableObject CreateEmbedded(GameplayAbilityAsset root, string name);
        void EnsureIds(ScriptableObject asset, bool recordUndo);
        void BuildDefinition(ScriptableObject asset);
        string AttachmentError(ScriptableObject asset);
        float Duration(ScriptableObject asset);
        string TrackLabel(string track);
        IEnumerable<SkillFlowEntry> Entries(SerializedObject asset);
        IEnumerable<SkillNodeCue> Cues(SerializedObject asset);
        void AddNodeFields(ISkillNodeFields node, SerializedObject asset);
        ISkillTimelineWorkspace OpenWorkspace(SkillDocument document);
        RuntimeSnapshot.PresentationTask Capture(GameplayAbilityTask task);
    }

    public interface ISkillNodeFields
    {
        void Field<T>(SerializedObject asset, string path, BaseField<T> field);
    }

    public interface ISkillTimelineEffectSlots
    {
        IEnumerable<SkillTimelineEffectSlot> EffectSlots(SerializedObject asset);
    }
    public readonly struct SkillTimelineEffectSlot
    {
        public readonly string Id, Track, Label;
        public readonly SerializedProperty Effect;
        public SkillTimelineEffectSlot(string id, string track, string label, SerializedProperty effect)
        { Id = id; Track = track; Label = label; Effect = effect; }
    }

    public readonly struct SkillFlowEntry
    {
        public readonly string Id, Label, PropertyPath;
        public readonly SerializedProperty Actions;
        public readonly float Time;
        public SkillFlowEntry(string id, string label, string propertyPath, SerializedProperty actions, float time)
        { Id = id; Label = label; PropertyPath = propertyPath; Actions = actions; Time = time; }
    }

    public readonly struct SkillNodeCue
    {
        public readonly string Tag, Track, Id, Span;
        public SkillNodeCue(string tag, string track = null, string id = null, string span = null)
        { Tag = tag; Track = track; Id = id; Span = span; }
    }

    public interface ISkillTimelineWorkspace : IDisposable
    {
        VisualElement Timeline { get; }
        VisualElement Preview { get; }
        VisualElement Toolbar { get; }
        void SyncToolbar();
        void Fit();
    }

    public static class SkillElementLookup
    {
        public static SerializedProperty Find(SerializedObject so, string track, string id)
        {
            if (so == null || string.IsNullOrEmpty(track) || string.IsNullOrEmpty(id)) return null;
            var array = so.FindProperty(track);
            for (int i = 0; array != null && array.isArray && i < array.arraySize; i++)
            {
                var item = array.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative("id")?.stringValue == id) return item;
            }
            return null;
        }
    }
}
