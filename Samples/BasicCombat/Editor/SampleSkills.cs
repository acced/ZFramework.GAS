using System;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Editor;
using ZFramework.GAS.Unity;
using T = ZFramework.GAS.Samples.BasicCombat.Editor.KnightAnimations.Timing;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>
    /// 五个示例技能。效果与动画都内嵌为技能的子资产，一个技能一个文件；
    /// 时间轴上的事件、Cue 区间与标签窗口和动画关键帧（<see cref="KnightAnimations.Timing"/>）对齐。
    /// </summary>
    internal static class SampleSkills
    {
        public const int Health = 1, MaxHealth = 2, Attack = 3, Stamina = 4;

        public static GameplayAbilityAsset[] Create(string folder, GameObject projectileVisual)
        {
            SampleArt.EnsureFolder(folder);
            return new[] { Slash(folder), Bolt(folder, projectileVisual), Warcry(folder), Sanctuary(folder), Summon(folder) };
        }

        private static GameplayAbilityAsset Slash(string folder)
        {
            var ability = Ability(folder, "斩击", SampleTags.AbilitySlash);
            var cost = Effect(ability, "消耗 · 体力", EffectDurationPolicy.Instant, 0, so => Modifier(so, Stamina, -10));
            var cooldown = Cooldown(ability, .6f, SampleTags.CooldownSlash);
            var damage = Effect(ability, "伤害 · 斩击", EffectDurationPolicy.Instant, 0, so => Modifier(so, Health, 0, Attack, -1.2f), SampleTags.CueHit);
            var animation = Animation(ability, "动画 · 斩击", "Slash", T.SlashLength, so =>
            {
                Marker(so, T.SlashHit, "命中", damage, AnimationMarkerTarget.Target);
                Region(so, T.SlashTrailStart, T.SlashTrailEnd, SampleTags.CueSwordTrail);
                Window(so, .12f, .4f, SampleTags.SuperArmor);
                Window(so, .55f, .85f, SampleTags.ComboWindow);
            });
            return Bind(ability, cost, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        private static GameplayAbilityAsset Bolt(string folder, GameObject visual)
        {
            var ability = Ability(folder, "能量弹", SampleTags.AbilityBolt);
            var cost = Effect(ability, "消耗 · 体力", EffectDurationPolicy.Instant, 0, so => Modifier(so, Stamina, -15));
            var cooldown = Cooldown(ability, 1.5f, SampleTags.CooldownBolt);
            var damage = Effect(ability, "伤害 · 能量弹", EffectDurationPolicy.Instant, 0, so => Modifier(so, Health, -6, Attack, -1.6f), SampleTags.CueHit);
            var animation = Animation(ability, "动画 · 能量弹", "Bolt", T.BoltLength, so =>
            {
                Region(so, T.BoltChargeStart, T.BoltRelease, SampleTags.CueBoltCharge);
                var tasks = so.FindProperty("taskEvents"); tasks.arraySize = 1;
                var task = tasks.GetArrayElementAtIndex(0);
                task.FindPropertyRelative("time").floatValue = T.BoltRelease;
                task.FindPropertyRelative("spawner").managedReferenceValue = new ProjectileSpawner
                {
                    visualPrefab = visual, impactEffect = damage, speed = 13, radius = .3f, curveHeight = .25f, maxDistance = 18,
                };
            });
            return Bind(ability, cost, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        private static GameplayAbilityAsset Warcry(string folder)
        {
            var ability = Ability(folder, "战吼", SampleTags.AbilityWarcry);
            var cooldown = Cooldown(ability, 8, SampleTags.CooldownWarcry);
            var buff = Effect(ability, "增益 · 战意", EffectDurationPolicy.Duration, 6, so => Modifier(so, Attack, 10));
            var animation = Animation(ability, "动画 · 战吼", "Warcry", T.WarcryLength, so =>
            {
                Marker(so, T.WarcryShout, "怒吼", buff, AnimationMarkerTarget.Self);
                Region(so, T.WarcryAuraStart, T.WarcryLength, SampleTags.CueWarcryAura);
                Region(so, .2f, .9f, SampleTags.CueGroundTest);
                Window(so, .2f, .9f, SampleTags.SuperArmor);
            });
            return Bind(ability, null, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        private static GameplayAbilityAsset Sanctuary(string folder)
        {
            var ability = Ability(folder, "圣域引导", SampleTags.AbilitySanctuary);
            var cooldown = Cooldown(ability, 10, SampleTags.CooldownSanctuary);
            var heal = Effect(ability, "治疗 · 圣域", EffectDurationPolicy.Duration, T.SanctuaryEnd - T.SanctuaryBegin, so =>
            {
                so.FindProperty("period").floatValue = .3f;
                so.FindProperty("executePeriodicOnApplication").boolValue = true;
                Modifier(so, Health, 6);
            });
            var animation = Animation(ability, "动画 · 圣域引导", "Sanctuary", T.SanctuaryLength, so =>
            {
                Marker(so, T.SanctuaryBegin, "开始引导", heal, AnimationMarkerTarget.Self);
                Region(so, T.SanctuaryBegin, T.SanctuaryEnd, SampleTags.CueSanctuary);
                Window(so, T.SanctuaryBegin, T.SanctuaryEnd, SampleTags.Channeling);
            });
            return Bind(ability, null, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        private static GameplayAbilityAsset Summon(string folder)
        {
            var ability = Ability(folder, "召唤", SampleTags.AbilitySummon);
            var cooldown = Cooldown(ability, 12, SampleTags.CooldownSummon);
            var animation = Animation(ability, "动画 · 召唤", "Summon", T.SummonLength, so =>
            {
                // 纯回调事件：召唤什么、召唤到哪由宿主决定。
                Marker(so, T.SummonStrike, "召唤", null, AnimationMarkerTarget.Target);
                Region(so, T.SummonCircleStart, T.SummonCircleEnd, SampleTags.CueSummon);
            });
            return Bind(ability, null, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        // ───────────── 构建辅助 ─────────────

        private static GameplayAbilityAsset Ability(string folder, string name, string abilityTag)
        {
            var ability = ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            AssetDatabase.CreateAsset(ability, folder + "/" + name + ".asset");
            Edit(ability, so => Tags(so.FindProperty("abilityTags"), abilityTag));
            return ability;
        }

        private static GameplayEffectAsset Effect(GameplayAbilityAsset owner, string name, EffectDurationPolicy policy, float duration,
            Action<SerializedObject> configure, string cueTag = null)
        {
            var effect = SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(owner, name);
            Edit(effect, so =>
            {
                so.FindProperty("durationPolicy").enumValueIndex = (int)policy;
                so.FindProperty("duration").floatValue = duration;
                if (cueTag != null) Tags(so.FindProperty("cueTags"), cueTag);
                configure(so);
            });
            return effect;
        }

        private static GameplayEffectAsset Cooldown(GameplayAbilityAsset owner, float seconds, string tag)
            => Effect(owner, $"冷却 · {seconds:0.#}s", EffectDurationPolicy.Duration, seconds, so => Tags(so.FindProperty("tags.granted"), tag));

        /// <summary>追加一条修改器；<paramref name="captureAttribute"/> 不小于 0 时按来源属性缩放。</summary>
        private static void Modifier(SerializedObject so, int attribute, float constant, int captureAttribute = -1, float coefficient = 1)
        {
            var list = so.FindProperty("modifiers");
            var m = list.GetArrayElementAtIndex(list.arraySize++);
            m.FindPropertyRelative("targetAttributeId").intValue = attribute;
            m.FindPropertyRelative("operation").enumValueIndex = (int)ModifierOperation.Add;
            var magnitude = m.FindPropertyRelative("magnitude");
            magnitude.FindPropertyRelative("constant").floatValue = constant;
            magnitude.FindPropertyRelative("kind").enumValueIndex = (int)(captureAttribute >= 0 ? MagnitudeKind.AttributeScaled : MagnitudeKind.Fixed);
            magnitude.FindPropertyRelative("captureAttributeId").intValue = Mathf.Max(0, captureAttribute);
            magnitude.FindPropertyRelative("coefficient").floatValue = coefficient;
            magnitude.FindPropertyRelative("captureFrom").enumValueIndex = (int)MagnitudeCapture.Source;
        }

        private static SkillAnimationAsset Animation(GameplayAbilityAsset owner, string name, string state, float length, Action<SerializedObject> timeline)
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(owner, name);
            Edit(animation, so =>
            {
                so.FindProperty("clipName").stringValue = "Base Layer." + state;
                so.FindProperty("duration").floatValue = length;
                so.FindProperty("frameRate").floatValue = 30;
                timeline(so);
            });
            TimelineCommands.EnsureIds(animation);
            return animation;
        }

        private static void Marker(SerializedObject so, float time, string key, GameplayEffectAsset effect, AnimationMarkerTarget target)
        {
            var list = so.FindProperty("markers");
            var m = list.GetArrayElementAtIndex(list.arraySize++);
            m.FindPropertyRelative("time").floatValue = time;
            m.FindPropertyRelative("key").stringValue = key;
            m.FindPropertyRelative("effect").objectReferenceValue = effect;
            m.FindPropertyRelative("target").enumValueIndex = (int)target;
        }

        private static void Region(SerializedObject so, float start, float end, string cueTag)
        {
            var list = so.FindProperty("cueRegions");
            var r = list.GetArrayElementAtIndex(list.arraySize++);
            r.FindPropertyRelative("start").floatValue = start;
            r.FindPropertyRelative("end").floatValue = end;
            r.FindPropertyRelative("cueTag.m_Name").stringValue = cueTag;
        }

        private static void Window(SerializedObject so, float start, float end, params string[] tags)
        {
            var list = so.FindProperty("tagWindows");
            var w = list.GetArrayElementAtIndex(list.arraySize++);
            w.FindPropertyRelative("start").floatValue = start;
            w.FindPropertyRelative("end").floatValue = end;
            Tags(w.FindPropertyRelative("tags"), tags);
        }

        private static void Tags(SerializedProperty array, params string[] names)
        {
            array.arraySize = names.Length;
            for (int i = 0; i < names.Length; i++) array.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue = names[i];
        }

        private static GameplayAbilityAsset Bind(GameplayAbilityAsset ability, GameplayEffectAsset cost, GameplayEffectAsset cooldown,
            SkillAnimationAsset animation, Action<SerializedObject> extra)
        {
            Edit(ability, so =>
            {
                so.FindProperty("cost").objectReferenceValue = cost;
                so.FindProperty("cooldown").objectReferenceValue = cooldown;
                so.FindProperty("animation").objectReferenceValue = animation;
                extra(so);
            });
            return ability;
        }

        private static void Edit(UnityEngine.Object target, Action<SerializedObject> change)
        {
            using var so = new SerializedObject(target);
            change(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
