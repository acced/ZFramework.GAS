using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Editor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    internal static class BasicCombatSkillTemplates
    {
        private const string Menu = "Tools/ZFramework GAS/BasicCombat/技能模板/";
        private static void Choose(SkillTemplate template) =>
            SkillTemplateRegistry.Choose(template, asset => SkillEditorWindow.Open().Show(asset));

        [MenuItem(Menu + "近战普攻")] private static void Melee() => Choose(new MeleeTemplate());
        [MenuItem(Menu + "投射物")] private static void Projectile() => Choose(new ProjectileTemplate());
        [MenuItem(Menu + "狂暴 Buff")] private static void Buff() => Choose(new BuffTemplate());
        [MenuItem(Menu + "持续伤害（打断后继续）")] private static void DamageOverTime() => Choose(new DamageOverTimeTemplate());
        [MenuItem(Menu + "引导（随施放取消）")] private static void Channel() => Choose(new ChannelTemplate());
        [MenuItem(Menu + "召唤骨架")] private static void Summon() => Choose(new SummonTemplate());

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
        internal static void Animation(GameplayAbilityAsset root, string label, float length, params SkillAction[] actions)
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(root, "Attack · 动画");
            SkillTimelineAuthoring.Edit(animation, t => {
                t.Clip("Attack", length);
                t.Marker("trigger", length * .45f, label, actions);
            }, recordUndo: true);
            SkillAssetFactory.SetMainAnimation(root, animation);
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
            var cost = BasicCombatSkillTemplates.Effect(ability, "消耗 · 体力", EffectDurationPolicy.Instant, 0, 4, -10);
            BasicCombatSkillTemplates.Bind(ability, "cost", cost); BasicCombatSkillTemplates.Cooldown(ability, .8f);
            var damage = BasicCombatSkillTemplates.Effect(ability, "伤害 · 挥砍", EffectDurationPolicy.Instant, 0, 1, 0);
            using (var so = new SerializedObject(damage))
            {
                var m = so.FindProperty("modifiers").GetArrayElementAtIndex(0).FindPropertyRelative("magnitude");
                m.FindPropertyRelative("kind").enumValueIndex = 1; m.FindPropertyRelative("captureAttributeId").intValue = 3;
                m.FindPropertyRelative("coefficient").floatValue = -1.2f; so.ApplyModifiedProperties();
            }
            BasicCombatSkillTemplates.Animation(ability, "命中", .8f, new ApplyEffectAction { effect = damage });
        }
    }
    public sealed class ProjectileTemplate : SkillTemplate
    {
        public override string DisplayName => "投射物";
        public override void Build(GameplayAbilityAsset ability)
        {
            var damage = BasicCombatSkillTemplates.Effect(ability, "伤害 · 投射物", EffectDurationPolicy.Instant, 0, 1, -20);
            BasicCombatSkillTemplates.Animation(ability, "发射", 1, new LaunchProjectileAction {
                onHit = new SkillAction[]{ new ApplyEffectAction { effect = damage } }
            });
            BasicCombatSkillTemplates.Cooldown(ability, 1);
        }
    }
    public sealed class BuffTemplate : SkillTemplate
    {
        public override string DisplayName => "狂暴 Buff";
        public override void Build(GameplayAbilityAsset ability)
        {
            var buff = BasicCombatSkillTemplates.Effect(ability, "狂暴 · 攻击加成", EffectDurationPolicy.Duration, 5, 3, 10);
            BasicCombatSkillTemplates.Animation(ability, "施加增益", .6f, new ApplyEffectAction {
                effect = buff, targets = new TargetQuery { kind = TargetQueryKind.Self }
            });
            BasicCombatSkillTemplates.Cooldown(ability, 8);
        }
    }
    public sealed class ChannelTemplate : SkillTemplate
    {
        public override string DisplayName => "引导（随施放取消）";
        public override void Build(GameplayAbilityAsset ability)
        {
            var hit = BasicCombatSkillTemplates.Effect(ability, "引导 · 单次伤害", EffectDurationPolicy.Instant, 0, 1, -5);
            BasicCombatSkillTemplates.Animation(ability, "开始引导", 3, new CreateAreaAction {
                targets = new TargetQuery { kind = TargetQueryKind.FixedTarget }, ownership = SkillTaskOwnership.Ability,
                duration = new SkillNumber(3), period = new SkillNumber(.5f),
                onPeriod = new SkillAction[] { new ApplyEffectAction { effect = hit } }
            });
        }
    }
    public sealed class DamageOverTimeTemplate : SkillTemplate
    {
        public override string DisplayName => "持续伤害（打断后继续）";
        public override void Build(GameplayAbilityAsset ability)
        {
            var effect = BasicCombatSkillTemplates.Effect(ability, "持续伤害", EffectDurationPolicy.Duration, 3, 1, -5);
            using (var so = new SerializedObject(effect)) { so.FindProperty("period").floatValue = .5f; so.ApplyModifiedProperties(); }
            BasicCombatSkillTemplates.Animation(ability, "施加持续伤害", 3, new ApplyEffectAction { effect = effect });
        }
    }
    public sealed class SummonTemplate : SkillTemplate
    {
        public override string DisplayName => "召唤骨架";
        public override void Build(GameplayAbilityAsset ability) =>
            BasicCombatSkillTemplates.Animation(ability, "生成", 1.5f, new SpawnPrefabAction());
    }
}
