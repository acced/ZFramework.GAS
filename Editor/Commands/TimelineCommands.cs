using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public static class TimelineCommands
    {
        public static SerializedProperty Find(SerializedObject so, string track, string id)
        {
            var array = so.FindProperty(track);
            if (array == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < array.arraySize; i++)
            {
                var item = array.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative("id")?.stringValue == id) return item;
            }
            return null;
        }
        public static bool EnsureIds(SkillAnimationAsset animation, bool recordUndo = false)
        {
            using var so = new SerializedObject(animation);
            bool changed = false;
            var seen = new HashSet<string>();
            foreach (var track in TrackTypeRegistry.Types)
            {
                var array = so.FindProperty(track.PropertyPath);
                if (array == null) continue;
                for (int i = 0; i < array.arraySize; i++)
                {
                    var id = array.GetArrayElementAtIndex(i).FindPropertyRelative("id");
                    if (id != null && (string.IsNullOrEmpty(id.stringValue) || !seen.Add(id.stringValue)))
                    { id.stringValue = Guid.NewGuid().ToString("N"); seen.Add(id.stringValue); changed = true; }
                }
            }
            if (changed) { if (recordUndo) so.ApplyModifiedProperties(); else so.ApplyModifiedPropertiesWithoutUndo(); }
            return changed;
        }
        public static string Add(SkillDocument doc, ITrackType track, float time, UnityEngine.Object dropped = null)
        {
            var animation = doc.Root?.Animation;
            if (animation == null) { doc.Report("先添加动画，再编辑时间轴"); return null; }
            string id = null;
            EditCommand.Run(doc, "添加" + track.DisplayName, animation, so =>
            {
                var array = so.FindProperty(track.PropertyPath);
                int index = array.arraySize++; var item = array.GetArrayElementAtIndex(index);
                track.InitializeNew(item, Mathf.Clamp(time, 0, animation.Duration), dropped);
                id = item.FindPropertyRelative("id").stringValue;
            }, true, track.PropertyPath);
            doc.Selection.Select(animation, track.PropertyPath, id);
            return id;
        }
        public static void Delete(SkillDocument doc, string track, IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            EditCommand.Run(doc, "删除时间轴元素", doc.Root.Animation, so =>
            {
                var array = so.FindProperty(track);
                for (int i = array.arraySize - 1; i >= 0; i--)
                    if (set.Contains(array.GetArrayElementAtIndex(i).FindPropertyRelative("id").stringValue)) array.DeleteArrayElementAtIndex(i);
            }, true, track);
            doc.Selection.Select(doc.Root.Animation);
        }
        [MenuItem("Tools/ZFramework GAS/迁移/为时间轴元素补 ID")]
        public static void MigrateIds()
        {
            var paths = new HashSet<string>(); int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:SkillAnimationAsset")) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            foreach (string guid in AssetDatabase.FindAssets("t:GameplayAbilityAsset")) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            foreach (string path in paths)
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset is SkillAnimationAsset animation && EnsureIds(animation)) { AssetDatabase.SaveAssetIfDirty(animation); count++; }
            Debug.Log($"GAS：已为 {count} 个动画补齐稳定 ID。");
        }
    }
}
