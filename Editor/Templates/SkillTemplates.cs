using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public abstract class SkillTemplate
    {
        public abstract string DisplayName { get; }
        public abstract void Build(GameplayAbilityAsset ability);
    }
    public static class SkillTemplateRegistry
    {
        public static IReadOnlyList<SkillTemplate> Templates => TypeCache.GetTypesDerivedFrom<SkillTemplate>()
            .Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null).Select(t => (SkillTemplate)Activator.CreateInstance(t)).OrderBy(t => t.DisplayName).ToArray();
        public static GameplayAbilityAsset Create(string path, SkillTemplate template)
        {
            var ability = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); ability.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(ability, path);
            Undo.RegisterCreatedObjectUndo(ability, "新建技能");
            template.Build(ability); EditorUtility.SetDirty(ability); AssetDatabase.SaveAssetIfDirty(ability);
            SkillIndex.Invalidate(); return ability;
        }
        public static void Choose(SkillTemplate template, Action<GameplayAbilityAsset> open)
        {
            string path = EditorUtility.SaveFilePanelInProject("新建技能 · " + template.DisplayName, template.DisplayName, "asset", "效果和动画将默认存为技能的子资产");
            if (string.IsNullOrEmpty(path)) return;
            int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("新建技能「" + template.DisplayName + "」");
            var ability = Create(path, template); Undo.CollapseUndoOperations(group); open(ability);
        }
        internal static GameplayEffectAsset Effect(GameplayAbilityAsset root, string name, EffectDurationPolicy policy, float duration, int attribute = -1, float magnitude = 0)
        {
            var effect = SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root, name);
            using var so = new SerializedObject(effect); so.FindProperty("durationPolicy").enumValueIndex = (int)policy; so.FindProperty("duration").floatValue = duration;
            if (attribute >= 0)
            {
                var modifiers = so.FindProperty("modifiers"); modifiers.arraySize = 1; var m = modifiers.GetArrayElementAtIndex(0);
                m.FindPropertyRelative("targetAttributeId").intValue = attribute; m.FindPropertyRelative("magnitude.constant").floatValue = magnitude;
            }
            so.ApplyModifiedProperties(); return effect;
        }
        internal static void Bind(GameplayAbilityAsset ability, string field, ScriptableObject asset)
        { using var so = new SerializedObject(ability); so.FindProperty(field).objectReferenceValue = asset; so.ApplyModifiedProperties(); }
        internal static SkillAnimationAsset Animation(GameplayAbilityAsset root, GameplayEffectAsset hit, string key, float length, bool self = false)
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(root, "Attack · 动画");
            using var so = new SerializedObject(animation); so.FindProperty("clipName").stringValue = "Attack"; so.FindProperty("duration").floatValue = length;
            var markers = so.FindProperty("markers"); markers.arraySize = 1; var marker = markers.GetArrayElementAtIndex(0);
            new EventTrack().InitializeNew(marker, length * .45f, hit); marker.FindPropertyRelative("key").stringValue = key;
            marker.FindPropertyRelative("target").enumValueIndex = self ? 1 : 0;
            so.ApplyModifiedProperties(); Bind(root, "animation", animation); return animation;
        }
        internal static void Cooldown(GameplayAbilityAsset root, float duration)
        {
            var effect = Effect(root, "冷却", EffectDurationPolicy.Duration, duration);
            using var so = new SerializedObject(effect); var tags = so.FindProperty("tags.granted"); tags.arraySize = 1;
            tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Cooldown." + root.name;
            so.ApplyModifiedProperties(); Bind(root, "cooldown", effect);
        }
    }
    public sealed class MeleeTemplate : SkillTemplate
    {
        public override string DisplayName => "近战普攻";
        public override void Build(GameplayAbilityAsset ability)
        {
            var cost = SkillTemplateRegistry.Effect(ability, "消耗 · 体力", EffectDurationPolicy.Instant, 0, 4, -10);
            SkillTemplateRegistry.Bind(ability, "cost", cost); SkillTemplateRegistry.Cooldown(ability, .8f);
            var damage = SkillTemplateRegistry.Effect(ability, "伤害 · 挥砍", EffectDurationPolicy.Instant, 0, 1, 0);
            using (var so = new SerializedObject(damage))
            {
                var m = so.FindProperty("modifiers").GetArrayElementAtIndex(0).FindPropertyRelative("magnitude");
                m.FindPropertyRelative("kind").enumValueIndex = 1; m.FindPropertyRelative("captureAttributeId").intValue = 3;
                m.FindPropertyRelative("coefficient").floatValue = -1.2f; so.ApplyModifiedProperties();
            }
            SkillTemplateRegistry.Animation(ability, damage, "命中", .8f);
        }
    }
    public sealed class ProjectileTemplate : SkillTemplate
    {
        public override string DisplayName => "投射物";
        public override void Build(GameplayAbilityAsset ability)
        {
            var damage = SkillTemplateRegistry.Effect(ability, "伤害 · 投射物", EffectDurationPolicy.Instant, 0, 1, -20);
            var animation = SkillTemplateRegistry.Animation(ability, null, "发射", 1);
            using var so = new SerializedObject(animation); so.FindProperty("markers").ClearArray(); var array = so.FindProperty("taskEvents"); array.arraySize = 1;
            var item = array.GetArrayElementAtIndex(0); new TaskTrack().InitializeNew(item, .4f, null);
            item.FindPropertyRelative("spawner").managedReferenceValue = new ProjectileSpawner { impactEffect = damage };
            so.ApplyModifiedProperties(); SkillTemplateRegistry.Cooldown(ability, 1);
        }
    }
    public sealed class BuffTemplate : SkillTemplate
    {
        public override string DisplayName => "狂暴 Buff";
        public override void Build(GameplayAbilityAsset ability)
        {
            var buff = SkillTemplateRegistry.Effect(ability, "狂暴 · 攻击加成", EffectDurationPolicy.Duration, 5, 3, 10);
            SkillTemplateRegistry.Animation(ability, buff, "施加增益", .6f, true); SkillTemplateRegistry.Cooldown(ability, 8);
        }
    }
    public sealed class ChannelTemplate : SkillTemplate
    {
        public override string DisplayName => "引导";
        public override void Build(GameplayAbilityAsset ability)
        {
            var effect = SkillTemplateRegistry.Effect(ability, "引导 · 周期效果", EffectDurationPolicy.Duration, 3, 1, -5);
            using (var so = new SerializedObject(effect)) { so.FindProperty("period").floatValue = .5f; so.ApplyModifiedProperties(); }
            SkillTemplateRegistry.Animation(ability, effect, "开始引导", 3);
        }
    }
    public sealed class SummonTemplate : SkillTemplate
    {
        public override string DisplayName => "召唤骨架";
        public override void Build(GameplayAbilityAsset ability) => SkillTemplateRegistry.Animation(ability, null, "召唤（宿主回调）", 1.5f);
    }
}
