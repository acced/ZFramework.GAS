using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public static class SkillSemanticDiff
    {
        [Serializable] public sealed class Entry { public string key, value, label; }
        [Serializable] public sealed class Snapshot
        { public int version = 1; public string skill, layout; public List<Entry> entries = new(); }
        public readonly struct Difference
        {
            public readonly string Key, Label, Before, After;
            public Difference(string key, string label, string before, string after) { Key = key; Label = label; Before = before; After = after; }
        }
        private static readonly Regex Index = new Regex(@"\.Array\.data\[(\d+)\]", RegexOptions.Compiled);
        public static Snapshot Capture(GameplayAbilityAsset ability)
        {
            if (ability == null) throw new ArgumentNullException(nameof(ability));
            var snapshot = new Snapshot { skill = ability.DisplayName, layout = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(ability))?.userData ?? "" };
            using var document = new SkillDocument(); document.Open(ability, false);
            foreach (var asset in document.Assets)
            {
                string assetKey = asset == ability ? "skill" : AssetReferences.Key(asset);
                var so = document.Serialized(asset); var property = so.GetIterator();
                bool children = true;
                while (property.Next(children))
                {
                    children = property.propertyType != SerializedPropertyType.String;
                    if (property.propertyPath == "m_Script") continue;
                    string path = StablePath(so, property.propertyPath);
                    string value = Value(property);
                    if (value != null) snapshot.entries.Add(new Entry { key = assetKey + "/" + path, value = value, label = asset.name + " / " + path });
                    if (property.isArray && property.propertyType != SerializedPropertyType.String && !property.propertyPath.EndsWith(".Array", StringComparison.Ordinal))
                    {
                        var order = new List<string>();
                        for (int i = 0; i < property.arraySize; i++)
                        { var id = property.GetArrayElementAtIndex(i).FindPropertyRelative("id"); if (id != null) order.Add(id.stringValue); }
                        if (order.Count > 0) snapshot.entries.Add(new Entry { key = assetKey + "/" + path + "/order", value = string.Join(",", order), label = asset.name + " / " + path + " 顺序" });
                    }
                }
            }
            snapshot.entries.Sort((a, b) => string.CompareOrdinal(a.key, b.key)); return snapshot;
        }
        public static IReadOnlyList<Difference> Compare(Snapshot before, Snapshot after)
        {
            if (before.version != 1 || after.version != 1) throw new InvalidDataException("不支持的技能快照版本。");
            var old = before.entries.ToDictionary(e => e.key); var next = after.entries.ToDictionary(e => e.key);
            var differences = new List<Difference>();
            foreach (var key in old.Keys.Concat(next.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal))
            {
                old.TryGetValue(key, out var a); next.TryGetValue(key, out var b);
                if (a?.value != b?.value) differences.Add(new Difference(key, b?.label ?? a.label, a?.value, b?.value));
            }
            return differences;
        }
        private static string StablePath(SerializedObject so, string path)
            => Index.Replace(path, match =>
            {
                var element = so.FindProperty(path.Substring(0, match.Index + match.Length));
                var id = element?.FindPropertyRelative("id");
                return id != null && !string.IsNullOrEmpty(id.stringValue) ? "[id=" + id.stringValue + "]" : "[" + match.Groups[1].Value + "]";
            });
        private static string Value(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.ArraySize: return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float: return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Enum: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == null ? "null" : AssetReferences.Key(property.objectReferenceValue);
                case SerializedPropertyType.ManagedReference: return property.managedReferenceFullTypename;
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                case SerializedPropertyType.Color: return property.colorValue.ToString("R");
                case SerializedPropertyType.AnimationCurve:
                    return string.Join(";", property.animationCurveValue.keys.Select(k => string.Join(",", new[] { k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))))) + "/" + property.animationCurveValue.preWrapMode + "/" + property.animationCurveValue.postWrapMode;
                default: return null;
            }
        }
    }

}
