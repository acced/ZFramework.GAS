using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public enum PortKind { Cost, Cooldown, Animation, Marker, Applied, Periodic, Removed }
    public readonly struct PortId : IEquatable<PortId>
    {
        public readonly PortKind Kind;
        public readonly string ElementId;
        public PortId(PortKind kind, string id = null) { Kind = kind; ElementId = id; }
        public bool Equals(PortId other) => Kind == other.Kind && ElementId == other.ElementId;
        public override bool Equals(object obj) => obj is PortId other && Equals(other);
        public override int GetHashCode() => ((int)Kind * 397) ^ (ElementId?.GetHashCode() ?? 0);
        public string Label => Kind switch { PortKind.Cost => "消耗", PortKind.Cooldown => "冷却", PortKind.Animation => "动画", PortKind.Marker => "事件", PortKind.Applied => "生效时", PortKind.Periodic => "每周期", _ => "移除时" };
        public Type AcceptedType => Kind == PortKind.Animation ? typeof(SkillAnimationAsset) : typeof(GameplayEffectAsset);
        public bool Single => Kind <= PortKind.Marker;
        public EffectTrigger Trigger => Kind == PortKind.Periodic ? EffectTrigger.Periodic : Kind == PortKind.Removed ? EffectTrigger.Removed : EffectTrigger.Applied;
    }
    public readonly struct AssetLink
    {
        public readonly ScriptableObject Parent, Child;
        public readonly PortId Port;
        public AssetLink(ScriptableObject parent, PortId port, ScriptableObject child) { Parent = parent; Port = port; Child = child; }
        public string Key => AssetReferences.Key(Parent) + "/" + Port.Kind + "/" + Port.ElementId + "/" + AssetReferences.Key(Child);
    }
    public static class PortRules
    {
        public static IEnumerable<AssetLink> Links(ScriptableObject asset)
        {
            using var so = new SerializedObject(asset);
            if (asset is GameplayAbilityAsset)
            {
                foreach (var pair in new[] { ("cost", PortKind.Cost), ("cooldown", PortKind.Cooldown), ("animation", PortKind.Animation) })
                    if (so.FindProperty(pair.Item1).objectReferenceValue is ScriptableObject child)
                        yield return new AssetLink(asset, new PortId(pair.Item2), child);
            }
            else if (asset is SkillAnimationAsset)
            {
                var markers = so.FindProperty("markers");
                for (int i = 0; i < markers.arraySize; i++)
                {
                    var marker = markers.GetArrayElementAtIndex(i);
                    if (marker.FindPropertyRelative("effect").objectReferenceValue is ScriptableObject child)
                        yield return new AssetLink(asset, new PortId(PortKind.Marker, marker.FindPropertyRelative("id").stringValue), child);
                }
            }
            else if (asset is GameplayEffectAsset)
            {
                var children = so.FindProperty("conditionalEffects");
                for (int i = 0; i < children.arraySize; i++)
                {
                    var child = children.GetArrayElementAtIndex(i);
                    if (child.FindPropertyRelative("effect").objectReferenceValue is ScriptableObject effect)
                    {
                        var trigger = (EffectTrigger)child.FindPropertyRelative("trigger").enumValueIndex;
                        yield return new AssetLink(asset, new PortId(trigger == EffectTrigger.Periodic ? PortKind.Periodic : trigger == EffectTrigger.Removed ? PortKind.Removed : PortKind.Applied), effect);
                    }
                }
            }
        }
        public static bool CanAttach(ScriptableObject parent, PortId port, ScriptableObject child, out string error, SkillDocument document = null)
        {
            error = null;
            if (parent == null || child == null || !port.AcceptedType.IsInstanceOfType(child)) error = "端口类型不匹配";
            else if (parent == child) error = "不能引用自身";
            else if (parent is GameplayAbilityAsset && port.Kind > PortKind.Animation || parent is SkillAnimationAsset && port.Kind != PortKind.Marker || parent is GameplayEffectAsset && port.Kind < PortKind.Applied)
                error = "该资产没有此端口";
            else
            {
                using var so = new SerializedObject(child);
                if (port.Kind == PortKind.Cost && so.FindProperty("durationPolicy").enumValueIndex != (int)EffectDurationPolicy.Instant)
                    error = "消耗必须是瞬时（Instant）效果";
                else if (port.Kind == PortKind.Cooldown && !HasGrantedTag(so)) error = "冷却效果必须至少授予一个标签";
                else if (port.Kind == PortKind.Animation && (string.IsNullOrWhiteSpace(so.FindProperty("clipName").stringValue) || !(so.FindProperty("duration").floatValue > 0) || float.IsInfinity(so.FindProperty("duration").floatValue)))
                    error = "动画需要片段名和有效的正时长";
                else if (port.Kind == PortKind.Marker)
                { using var owner = new SerializedObject(parent); if (TimelineCommands.Find(owner, "markers", port.ElementId) == null) error = "事件点已不存在"; }
                else if (parent is GameplayEffectAsset)
                {
                    if (Links(parent).Any(l => l.Port.Equals(port) && l.Child == child)) error = "同一时机已经引用该效果";
                    else if (Reaches(child, parent, new HashSet<ScriptableObject>(), 0)) error = "子效果链会形成循环或超过 8 层";
                    else if (document != null)
                    {
                        bool ExceedsDepth(ScriptableObject current, int depth, HashSet<ScriptableObject> path)
                        {
                            if (depth > 8 || !path.Add(current)) return true;
                            var children = Links(current).Select(l => l.Child);
                            if (current == parent) children = children.Append(child);
                            bool invalid = children.Any(c => ExceedsDepth(c, depth + 1, path));
                            path.Remove(current); return invalid;
                        }
                        if (document.Assets.OfType<GameplayEffectAsset>().Any(a => ExceedsDepth(a, 0, new HashSet<ScriptableObject>())))
                            error = "连接后完整子效果链会超过 8 层，或存在循环";
                    }
                }
            }
            return error == null;
        }
        private static bool Reaches(ScriptableObject node, ScriptableObject target, HashSet<ScriptableObject> path, int depth)
        {
            if (node == target || depth >= 8 || !path.Add(node)) return true;
            bool result = Links(node).Any(link => Reaches(link.Child, target, path, depth + 1));
            path.Remove(node); return result;
        }
        public static bool HasGrantedTag(SerializedObject so)
        {
            var list = so.FindProperty("tags.granted");
            for (int i = 0; list != null && i < list.arraySize; i++)
                if (!string.IsNullOrWhiteSpace(list.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue)) return true;
            return false;
        }
        public static void Write(SerializedObject so, PortId port, ScriptableObject child, bool remove = false)
        {
            if (port.Kind <= PortKind.Animation)
                so.FindProperty(port.Kind == PortKind.Cost ? "cost" : port.Kind == PortKind.Cooldown ? "cooldown" : "animation").objectReferenceValue = remove ? null : child;
            else if (port.Kind == PortKind.Marker)
            {
                var marker = TimelineCommands.Find(so, "markers", port.ElementId);
                if (marker != null) marker.FindPropertyRelative("effect").objectReferenceValue = remove ? null : child;
            }
            else
            {
                var list = so.FindProperty("conditionalEffects");
                if (remove)
                {
                    for (int i = list.arraySize - 1; i >= 0; i--)
                    {
                        var item = list.GetArrayElementAtIndex(i);
                        if (item.FindPropertyRelative("effect").objectReferenceValue == child && item.FindPropertyRelative("trigger").enumValueIndex == (int)port.Trigger)
                            list.DeleteArrayElementAtIndex(i);
                    }
                }
                else
                {
                    int index = list.arraySize++; var item = list.GetArrayElementAtIndex(index);
                    item.FindPropertyRelative("effect").objectReferenceValue = child;
                    item.FindPropertyRelative("trigger").enumValueIndex = (int)port.Trigger;
                }
            }
        }
        public static bool Attach(SkillDocument doc, ScriptableObject parent, PortId port, ScriptableObject child)
        {
            if (!CanAttach(parent, port, child, out string error, doc)) { doc.Report(error); return false; }
            EditCommand.Run(doc, "连接" + port.Label, parent, so => Write(so, port, child), true);
            return true;
        }
    }
}
