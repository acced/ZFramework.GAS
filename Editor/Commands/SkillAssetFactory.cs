using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public static class SkillAssetFactory
    {
        public static bool IsEmbedded(GameplayAbilityAsset root, UnityEngine.Object asset)
            => root != null && asset != null && root != asset && AssetDatabase.GetAssetPath(asset) == AssetDatabase.GetAssetPath(root);
        public static T AddEmbedded<T>(GameplayAbilityAsset root, string name) where T : ScriptableObject
        {
            if (root == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(root))) throw new InvalidOperationException("先保存能力主资产。");
            var asset = ScriptableObject.CreateInstance<T>(); asset.name = name;
            AssetDatabase.AddObjectToAsset(asset, root);
            Undo.RegisterCreatedObjectUndo(asset, "创建" + name);
            EditorUtility.SetDirty(root); return asset;
        }
        public static ScriptableObject Create(SkillDocument doc, Type type, string name, EffectDurationPolicy policy,
            ScriptableObject parent = null, PortId? port = null)
        {
            ScriptableObject created = null;
            EditCommand.Transaction(doc, "创建" + name, doc.Root, () =>
            {
                created = type == typeof(SkillAnimationAsset) ? AddEmbedded<SkillAnimationAsset>(doc.Root, name) : AddEmbedded<GameplayEffectAsset>(doc.Root, name);
                using (var so = new SerializedObject(created))
                {
                    if (created is GameplayEffectAsset)
                    {
                        so.FindProperty("durationPolicy").enumValueIndex = (int)policy;
                        so.FindProperty("duration").floatValue = policy == EffectDurationPolicy.Duration ? 1 : 0;
                        if (port?.Kind == PortKind.Cooldown)
                        {
                            var tags = so.FindProperty("tags.granted"); tags.arraySize = 1;
                            tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Cooldown." + doc.Root.name;
                        }
                    }
                    else so.FindProperty("clipName").stringValue = "Attack";
                    so.ApplyModifiedProperties();
                }
                if (parent != null && port.HasValue)
                {
                    if (!PortRules.CanAttach(parent, port.Value, created, out string error, doc)) throw new InvalidOperationException(error);
                    using var so = new SerializedObject(parent); PortRules.Write(so, port.Value, created); so.ApplyModifiedProperties();
                }
            });
            doc.Selection.Select(created); return created;
        }
        // Copy the entire asset file, then explicitly remap only references to owned subassets.
        public static GameplayAbilityAsset Duplicate(GameplayAbilityAsset source, string destination)
        {
            string original = AssetDatabase.GetAssetPath(source);
            if (!AssetDatabase.CopyAsset(original, destination)) throw new IOException("无法复制技能资产。");
            var copied = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(destination);
            var originals = AssetDatabase.LoadAllAssetsAtPath(original);
            var copies = AssetDatabase.LoadAllAssetsAtPath(destination);
            var map = new Dictionary<Object, Object>();
            foreach (var item in originals)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out string _, out long localId);
                var match = copies.FirstOrDefault(c => { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c, out string _, out long id); return id == localId; });
                if (match != null) map[item] = match;
            }
            foreach (var item in copies.OfType<ScriptableObject>())
            {
                using var so = new SerializedObject(item);
                foreach (var pair in map) AssetReferences.Replace(so, pair.Key, pair.Value);
                so.ApplyModifiedPropertiesWithoutUndo();
                if (item is SkillAnimationAsset animation) TimelineCommands.EnsureIds(animation);
            }
            copied.name = Path.GetFileNameWithoutExtension(destination); EditorUtility.SetDirty(copied);
            AssetDatabase.SaveAssetIfDirty(copied); return copied;
        }
        public static ScriptableObject CopyNode(SkillDocument doc, ScriptableObject source)
        {
            if (!IsEmbedded(doc.Root, source)) { doc.AddLoose(source); return source; }
            ScriptableObject copy = null;
            EditCommand.Transaction(doc, "复制节点", source, () =>
            {
                var map = new Dictionary<Object, Object>();
                ScriptableObject Clone(ScriptableObject original)
                {
                    if (!IsEmbedded(doc.Root, original)) return original;
                    if (map.TryGetValue(original, out var existing)) return (ScriptableObject)existing;
                    var clone = Object.Instantiate(original); clone.name = original.name + " 副本";
                    AssetDatabase.AddObjectToAsset(clone, doc.Root); Undo.RegisterCreatedObjectUndo(clone, "复制节点"); map[original] = clone;
                    foreach (var child in AssetReferences.Children(original)) Clone(child);
                    return clone;
                }
                copy = Clone(source);
                foreach (var item in map.Values)
                {
                    using var so = new SerializedObject(item);
                    foreach (var pair in map) AssetReferences.Replace(so, pair.Key, pair.Value);
                    so.ApplyModifiedProperties();
                    if (item is SkillAnimationAsset animation) TimelineCommands.EnsureIds(animation);
                }
                EditorUtility.SetDirty(doc.Root);
            });
            doc.Selection.Select(copy); return copy;
        }
        public static List<string> FindReferences(Object target, string excludePath = null)
        {
            var found = new List<string>(); var paths = new HashSet<string>();
            foreach (string filter in new[] { "t:GameplayAbilityAsset", "t:GameplayEffectAsset", "t:SkillAnimationAsset" })
                foreach (string guid in AssetDatabase.FindAssets(filter)) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            foreach (string path in paths)
            {
                if (path == excludePath) continue;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path).OfType<ScriptableObject>())
                    if (asset != target && AssetReferences.Children(asset).Contains(target)) found.Add(path + " / " + asset.name);
            }
            return found;
        }
        public static void DeleteNode(SkillDocument doc, ScriptableObject asset)
        {
            if (asset == null || asset == doc.Root) return;
            bool embedded = IsEmbedded(doc.Root, asset);
            if (embedded)
            {
                var references = FindReferences(asset, AssetDatabase.GetAssetPath(doc.Root));
                if (references.Count > 0) { doc.Report("仍被 " + references.Count + " 处引用：" + string.Join("，", references)); return; }
            }
            else if (doc.Assets.Any(owner => AssetDatabase.GetAssetPath(owner) != AssetDatabase.GetAssetPath(doc.Root) && AssetReferences.Children(owner).Contains(asset)))
            { doc.Report("该节点由共享资产引用；先把上游效果内嵌，再删除此节点。"); return; }
            EditCommand.Transaction(doc, "删除节点「" + asset.name + "」", doc.Root, () =>
            {
                // Do not edit an external shared graph when removing its projection from this skill.
                foreach (var owner in doc.Assets.ToArray())
                {
                    if (owner == asset || AssetDatabase.GetAssetPath(owner) != AssetDatabase.GetAssetPath(doc.Root)) continue;
                    using var so = new SerializedObject(owner); AssetReferences.Replace(so, asset, null); so.ApplyModifiedProperties();
                }
                doc.RemoveLoose(asset);
                if (embedded) Undo.DestroyObjectImmediate(asset);
            });
            doc.Selection.Select(doc.Root);
        }
        public static ScriptableObject Convert(SkillDocument doc, ScriptableObject source, bool embed, string externalPath = null)
        {
            ScriptableObject copy = null;
            EditCommand.Transaction(doc, embed ? "内嵌资产" : "外置资产", doc.Root, () =>
            {
                copy = Object.Instantiate(source); copy.name = source.name;
                if (embed) AssetDatabase.AddObjectToAsset(copy, doc.Root);
                else AssetDatabase.CreateAsset(copy, externalPath);
                Undo.RegisterCreatedObjectUndo(copy, "转换资产");
                EditorUtility.SetDirty(doc.Root);
                EditorUtility.SetDirty(copy);
                foreach (var owner in doc.Assets.ToArray())
                {
                    if (AssetDatabase.GetAssetPath(owner) != AssetDatabase.GetAssetPath(doc.Root)) continue;
                    using var so = new SerializedObject(owner); AssetReferences.Replace(so, source, copy); so.ApplyModifiedProperties();
                }
                // The original external asset is deliberately retained; other projects may reference its GUID.
                if (!embed && FindReferences(source, AssetDatabase.GetAssetPath(doc.Root)).Count == 0) Undo.DestroyObjectImmediate(source);
            });
            doc.Selection.Select(copy); return copy;
        }
    }
}
