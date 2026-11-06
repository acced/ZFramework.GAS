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
        private const string CooldownParameter = "冷却", NormalDamage = "普通伤害系数", EmpoweredDamage = "强化伤害系数";

        public static GameplayAbilityAsset[] Create(string folder, GameObject projectileVisual, GameObject guardianPrefab,GameObject lightning,out GameplayAbilityAsset bounce)
        {
            SampleArt.EnsureFolder(folder);
            var skills = new[] { Slash(folder), Bolt(folder, projectileVisual,lightning,out bounce), Warcry(folder), Sanctuary(folder), Summon(folder, guardianPrefab) };
            ShareSlashModule(folder, skills[0]);
            return skills;
        }

        private static void ShareSlashModule(string folder, GameplayAbilityAsset slash)
        {
            string sharedFolder = folder + "/Shared"; SampleArt.EnsureFolder(sharedFolder);
            // Both the heavy-slash variant and its module must keep the very same effects.
            AssetDatabase.SaveAssetIfDirty(slash);
            foreach(var item in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(slash)))
                if(item is GameplayEffectAsset effect)MakeShared(effect,sharedFolder+"/斩击 · "+effect.name+".asset");
            // 冷却属于技能自身，留在斩击上；其余是命中模块用到的伤害与范围，抽成共享参数集。
            var parameters = ScriptableObject.CreateInstance<SkillParameterSetAsset>();
            parameters.parameters = Array.FindAll(slash.parameters, p => p.displayName != CooldownParameter);
            AssetDatabase.CreateAsset(parameters, sharedFolder + "/斩击参数.asset");
            slash.parameters = Array.FindAll(slash.parameters, p => p.displayName == CooldownParameter); slash.parameterSet = parameters;
            var module = ScriptableObject.CreateInstance<SkillActionModuleAsset>(); module.parameters = parameters;
            using (var so = new SerializedObject(slash.Animation))
            {
                var actions = so.FindProperty("markers.Array.data[0].actions");
                module.actions = new SkillAction[actions.arraySize];
                for (int i = 0; i < actions.arraySize; i++) module.actions[i] = (SkillAction)actions.GetArrayElementAtIndex(i).managedReferenceValue;
                AssetDatabase.CreateAsset(module, sharedFolder + "/扇形斩击动作.asset");
                // 共享集只含模块用到的参数，逐个绑定，让变体的覆盖传进模块。
                var bindings = Array.ConvertAll(parameters.parameters, p => new ModuleParameterBinding { moduleParameterId = p.Id, callerParameterId = p.Id });
                actions.arraySize = 1; actions.GetArrayElementAtIndex(0).managedReferenceValue = new CallModuleAction { module = module, bindings = bindings };
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            var variant = UnityEngine.Object.Instantiate(slash); variant.displayName = "重斩（共享模块变体）";
            var overrides = new System.Collections.Generic.List<SkillParameterOverride>();
            foreach (var parameter in parameters.parameters)
                if (parameter.displayName is NormalDamage or EmpoweredDamage) overrides.Add(new SkillParameterOverride { parameterId = parameter.Id, value = parameter.defaultValue * 1.5f });
            variant.parameterOverrides = overrides.ToArray();
            var axe = ScriptableObject.CreateInstance<SkillClipOverrideSet>();
            axe.clips = new[] { new SkillClipOverrideSet.Entry { original = slash.Animation.ClipName, replacement = "Base Layer.AxeSlash" } };
            AssetDatabase.CreateAsset(axe, sharedFolder + "/斧劈替换表.asset");
            variant.clipOverrides = axe;
            AssetDatabase.CreateAsset(variant, sharedFolder + "/重斩变体.asset"); EditorUtility.SetDirty(slash);
        }

        private static GameplayAbilityAsset Slash(string folder)
        {
            var ability = Ability(folder, "斩击", SampleTags.AbilitySlash);
            var cost = Effect(ability, "消耗 · 体力", EffectDurationPolicy.Instant, 0, so => Modifier(so, Stamina, -10));
            var cooldown = Cooldown(ability, .6f, SampleTags.CooldownSlash);
            var damage = Effect(ability, "伤害 · 斩击", EffectDurationPolicy.Instant, 0, so => Damage(so, ability, 1.2f, NormalDamage), SampleTags.CueHit);
            var empowered = Effect(ability, "伤害 · 强化斩击", EffectDurationPolicy.Instant, 0, so => Damage(so, ability, 2, EmpoweredDamage), SampleTags.CueHit);
            var animation = Animation(ability, "动画 · 斩击", "Slash", T.SlashLength, t =>
            {
                t.Marker("impact", T.SlashHit, "命中", new ConditionalAction {
                    requiredTags = new[] { new GameplayTags.GameplayTag { m_Name = SampleTags.Empowered } },
                    whenTrue = new SkillAction[] { new ApplyEffectAction { effect = empowered, targets = new TargetQuery {
                        kind = TargetQueryKind.Sector, relation = TargetRelation.Enemy, radius = Number(ability, "范围", 3), angle = new SkillNumber(110) } } },
                    whenFalse = new SkillAction[] { new ApplyEffectAction { effect = damage, targets = new TargetQuery { kind = TargetQueryKind.Sector, relation = TargetRelation.Enemy, radius = Number(ability, "基础范围", 2.5f), angle = new SkillNumber(90) } } }
                });
                t.Cue("trail", T.SlashTrailStart, T.SlashTrailEnd, SampleTags.CueSwordTrail);
                t.TagWindow("armor", .12f, .4f, SampleTags.SuperArmor);
                t.TagWindow("combo", .55f, .85f, SampleTags.ComboWindow);
            }, .08f, .2f);
            return Bind(ability, cost, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        private static GameplayAbilityAsset Bolt(string folder,GameObject visual,GameObject lightning,out GameplayAbilityAsset bounce)
        {
            var ability=Ability(folder,"分叉闪电",SampleTags.AbilityBolt);
            bounce=Ability(folder,"逐个弹射",SampleTags.AbilityBolt);
            ability.description="能量弹命中后，从同一命中点向附近敌人同时放电。";
            bounce.description="能量弹每次命中后从命中点搜索下一名敌人，再发射一枚飞弹。";
            string Parameter(string name,SkillParameterKind kind,float value){
                var p=new SkillParameter{displayName=name,kind=kind,defaultValue=value};
                ability.parameters=new System.Collections.Generic.List<SkillParameter>(ability.parameters){p}.ToArray();
                EditorUtility.SetDirty(ability);return p.Id;
            }
            string hop=Parameter("本跳序号",SkillParameterKind.Int,0),scale=Parameter("本跳伤害倍率",SkillParameterKind.Float,1);
            var limit=new SkillNumber(3){parameterId=Parameter("最多命中单位（含首击）",SkillParameterKind.Int,3)};
            var range=Number(ability,"搜索半径",3.5f);var retention=Number(ability,"每跳伤害保留",.8f);
            var speed=Number(ability,"速度",1.2f);var life=Number(ability,"寿命",6);
            var cost=Effect(ability,"消耗 · 体力",EffectDurationPolicy.Instant,0,so=>Modifier(so,Stamina,-15));
            var cooldown=Cooldown(ability,1.5f,SampleTags.CooldownBolt);
            var damage=Effect(ability,"伤害 · 能量弹",EffectDurationPolicy.Instant,0,so=>{
                Damage(so,ability,1.6f);
                so.FindProperty("modifiers").GetArrayElementAtIndex(0).FindPropertyRelative("magnitude.multiplierParameterId").stringValue=scale;
            },SampleTags.CueHit);
            SkillAction Hit()=>new ApplyEffectAction{effect=damage, targets=new TargetQuery{relation=TargetRelation.Enemy}};
            SelectTargetsAction Pick(SkillNumber n)=>new(){targets=new TargetQuery{kind=TargetQueryKind.Circle,relation=TargetRelation.Enemy,origin=SkillSpawnPosition.HitPoint,radius=range,maxTargets=64},
                count=n,actions=new SkillAction[]{new NextTargetStepAction()}};
            LaunchProjectileAction Shot(SkillSpawnPosition origin,params SkillAction[] onHit)=>new(){prefab=visual,origin=origin,
                onlySelectedTarget=true,collision=new TargetQuery{kind=TargetQueryKind.Circle,relation=TargetRelation.Enemy},
                speed=speed,lifetime=life,radius=.3f,arcHeight=.25f,onHit=onHit};
            string shared=folder+"/Shared";SampleArt.EnsureFolder(shared);
            var set=ScriptableObject.CreateInstance<SkillParameterSetAsset>();set.parameters=ability.parameters;
            AssetDatabase.CreateAsset(set,shared+"/能量弹参数.asset");ability.parameters=Array.Empty<SkillParameter>();
            ability.parameterSet=bounce.parameterSet=set;
            MakeShared(cost,shared+"/能量弹消耗.asset");MakeShared(cooldown,shared+"/能量弹冷却.asset");MakeShared(damage,shared+"/能量弹伤害.asset");
            void Configure(GameplayAbilityAsset variant,bool fork) {
                var sequence=new TargetSequenceAction{maxTargets=limit,retention=retention,indexParameterId=hop,scaleParameterId=scale,
                    first=new SkillAction[]{Shot(SkillSpawnPosition.Caster,Hit(),Pick(fork?limit:new SkillNumber(1)))},
                    next=fork?new SkillAction[]{new ShowTargetLinkAction{prefab=lightning},Hit()}:
                        new SkillAction[]{Shot(SkillSpawnPosition.HitPoint,Hit(),Pick(new SkillNumber(1)))}};
                var animation=Animation(variant,"动画 · "+variant.displayName,"Bolt",T.BoltLength,t=>{
                    t.Cue("charge",T.BoltChargeStart,T.BoltRelease,SampleTags.CueBoltCharge);t.Marker("release",T.BoltRelease,"发射",sequence);
                },.12f,.22f);
                Bind(variant,cost,cooldown,animation,so=>Tags(so.FindProperty("activationBlockedTags"),SampleTags.Channeling));
            }
            Configure(ability,true);Configure(bounce,false);return ability;
        }
        private static void MakeShared(GameplayEffectAsset effect,string path) {
            AssetDatabase.RemoveObjectFromAsset(effect);AssetDatabase.CreateAsset(effect,path);
        }

        private static GameplayAbilityAsset Warcry(string folder)
        {
            var ability = Ability(folder, "战吼", SampleTags.AbilityWarcry);
            var cooldown = Cooldown(ability, 8, SampleTags.CooldownWarcry);
            var buff = Effect(ability, "增益 · 战意", EffectDurationPolicy.Duration, 6, so => { Modifier(so, Attack, 10); Tags(so.FindProperty("tags.granted"), SampleTags.Empowered); });
            var animation = Animation(ability, "动画 · 战吼", "Warcry", T.WarcryLength, t =>
            {
                t.Marker("shout", T.WarcryShout, "怒吼", new ApplyEffectAction { effect = buff, targets = new TargetQuery { kind = TargetQueryKind.Self } });
                t.Cue("aura", T.WarcryAuraStart, T.WarcryLength, SampleTags.CueWarcryAura);
                t.Cue("ground", .2f, .9f, SampleTags.CueGroundTest);
                t.TagWindow("armor", .2f, .9f, SampleTags.SuperArmor);
            }, .15f, .28f);
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
            var animation = Animation(ability, "动画 · 圣域引导", "Sanctuary", T.SanctuaryLength, t =>
            {
                t.Marker("open", T.SanctuaryBegin, "开始引导", new CreateAreaAction {
                    targets = new TargetQuery { kind = TargetQueryKind.Circle, relation = TargetRelation.Ally, excludeSelf = false, radius = Number(ability, "范围", 3) },
                    duration = Number(ability, "引导时长", T.SanctuaryEnd - T.SanctuaryBegin), enterEffect = heal,
                    removeOnExit = true, ownership = SkillTaskOwnership.Ability
                });
                t.Cue("field", T.SanctuaryBegin, T.SanctuaryEnd, SampleTags.CueSanctuary);
                t.TagWindow("channel", T.SanctuaryBegin, T.SanctuaryEnd, SampleTags.Channeling);
            }, .2f, .35f);
            return Bind(ability, null, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        private static GameplayAbilityAsset Summon(string folder, GameObject guardianPrefab)
        {
            var ability = Ability(folder, "召唤", SampleTags.AbilitySummon);
            var cooldown = Cooldown(ability, 12, SampleTags.CooldownSummon);
            var pulse = Effect(ability, "伤害 · 召唤领域", EffectDurationPolicy.Instant, 0, so => Damage(so, ability, .4f), SampleTags.CueHit);
            var animation = Animation(ability, "动画 · 召唤", "Summon", T.SummonLength, t =>
            {
                t.Marker("spawn", T.SummonStrike, "召唤", new SpawnPrefabAction {
                    prefab = guardianPrefab, offset = T.SummonPoint, lifetime = Number(ability, "召唤寿命", 5), count = Number(ability, "召唤数量", 1), spacing = Vector3.right
                }, new CreateAreaAction { targets = new TargetQuery { kind = TargetQueryKind.TargetPoint, relation = TargetRelation.Enemy, radius = Number(ability, "领域范围", 2) },
                    duration = Number(ability, "领域寿命", 5), period = Number(ability, "领域周期", 1), ownership = SkillTaskOwnership.Scene,
                    onPeriod = new SkillAction[] { new ApplyEffectAction { effect = pulse } } });
                t.Cue("circle", T.SummonCircleStart, T.SummonCircleEnd, SampleTags.CueSummon);
            }, .15f, .28f);
            return Bind(ability, null, cooldown, animation, so => Tags(so.FindProperty("activationBlockedTags"), SampleTags.Channeling));
        }

        // ───────────── 构建辅助 ─────────────

        private static GameplayAbilityAsset Ability(string folder, string name, string abilityTag)
        {
            var ability = ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            AssetDatabase.CreateAsset(ability, folder + "/" + name + ".asset");
            ability.displayName = name;
            ability.description = "由动作、参数和时间轴配置的技能；修改标记显示名不会改变行为。";
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
        {
            var number = Number(owner, "冷却", seconds);
            return Effect(owner, "冷却", EffectDurationPolicy.Duration, seconds, so => {
                Tags(so.FindProperty("tags.granted"), tag);
                so.FindProperty("useDurationMagnitude").boolValue = true;
                so.FindProperty("durationMagnitude.kind").enumValueIndex = (int)MagnitudeKind.Parameter;
                so.FindProperty("durationMagnitude.parameterId").stringValue = number.parameterId;
            });
        }
        private static SkillNumber Number(GameplayAbilityAsset owner, string name, float value)
        {
            var parameter = new SkillParameter { displayName = name, defaultValue = value };
            if (name.EndsWith("伤害系数")) parameter.levels = new[] { value, value * 1.25f, value * 1.5f };
            var list = new System.Collections.Generic.List<SkillParameter>(owner.parameters) { parameter };
            owner.parameters = list.ToArray(); EditorUtility.SetDirty(owner);
            return new SkillNumber(value) { parameterId = parameter.Id };
        }
        private static void Damage(SerializedObject so, GameplayAbilityAsset ability, float scale, string parameterName = "伤害系数")
        {
            var number = Number(ability, parameterName, scale);
            Modifier(so, Health, 0, Attack, 1);
            var magnitude = so.FindProperty("modifiers").GetArrayElementAtIndex(0).FindPropertyRelative("magnitude");
            magnitude.FindPropertyRelative("kind").enumValueIndex = (int)MagnitudeKind.Calculation;
            magnitude.FindPropertyRelative("parameterId").stringValue = number.parameterId;
            magnitude.FindPropertyRelative("targetAttributeId").intValue = 5;
            magnitude.FindPropertyRelative("minimum").floatValue = 0;
            magnitude.FindPropertyRelative("maximum").floatValue = 99999;
            magnitude.FindPropertyRelative("resultScale").floatValue = -1;
        }

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

        private static SkillAnimationAsset Animation(GameplayAbilityAsset owner, string name, string state, float length, Action<SkillTimelineAuthoring> timeline,
            float blendIn = .12f, float blendOut = .2f)
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(owner, name);
            SkillTimelineAuthoring.Edit(animation, t =>
            {
                t.Clip("Base Layer." + state, length, 30, blendIn, blendOut);
                timeline(t);
            });
            // Presentation samples are authored on the same timeline as project-defined tracks.
            if(state=="Slash") animation.runtimeTracks=new SkillRuntimeTrack[]{
                new CameraShakeTrack{start=T.SlashHit,end=T.SlashHit+.09f,direction=Vector3.right,amount=AnimationCurve.Linear(0,.025f,1,0)},
                new HitStopTrack{start=T.SlashHit,end=T.SlashHit+.04f,realSeconds=.035f}};
            if(state=="Bolt") animation.runtimeTracks=new SkillRuntimeTrack[]{
                new LookAtTrack{start=.02f,end=T.BoltRelease,boneChain="Head",amount=AnimationCurve.Linear(0,0,1,.3f)}};
            if(state=="Warcry") animation.runtimeTracks=new SkillRuntimeTrack[]{
                new TimeScaleTrack{start=.1f,end=.28f,amount=AnimationCurve.Linear(0,.9f,1,1)}};
            EditorUtility.SetDirty(animation);
            TimelineCommands.EnsureIds(animation);
            return animation;
        }

        private static void Tags(SerializedProperty array, params string[] names)
        {
            array.arraySize = names.Length;
            for (int i = 0; i < names.Length; i++) array.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue = names[i];
        }

        private static GameplayAbilityAsset Bind(GameplayAbilityAsset ability, GameplayEffectAsset cost, GameplayEffectAsset cooldown,
            SkillAnimationAsset animation, Action<SerializedObject> extra)
        {
            SkillAssetFactory.SetMainAnimation(ability, animation);
            Edit(ability, so =>
            {
                so.FindProperty("cost").objectReferenceValue = cost;
                so.FindProperty("cooldown").objectReferenceValue = cooldown;
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
