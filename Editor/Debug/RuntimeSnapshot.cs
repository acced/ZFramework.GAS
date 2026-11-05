using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>一次只读采样；数值冻结在采集时刻，界面不再访问运行期的可变 Spec。</summary>
    public sealed class RuntimeSnapshot
    {
        public AbilitySystemComponent Owner { get; }
        public string UnitName { get; }
        public double CapturedAt { get; }
        public IReadOnlyList<Ability> Abilities { get; }
        public IReadOnlyList<Animation> Tasks { get; }
        public IReadOnlyList<Tag> Tags { get; }
        public IReadOnlyList<Attribute> Attributes { get; }
        public IReadOnlyList<Effect> Effects { get; }
        public IReadOnlyList<ActionState> Actions { get; }

        public sealed class ActionState
        {
            public readonly GameplayAbilityAsset Asset;
            public readonly string Id, State;
            public readonly bool Independent;
            internal ActionState(ISkillTaskState task, bool independent)
            { Asset = task.Execution.Asset; Id = task.ActionId; State = task.State; Independent = independent; }
        }

        public sealed class Ability
        {
            public readonly GameplayAbilityAsset Asset;
            public readonly string Name;
            public readonly bool Active, OnCooldown, TagGatePassed;
            public readonly float CooldownRemaining, CooldownDuration;
            public readonly int Level;
            public readonly AbilityActivationCheck Failure;
            public readonly IReadOnlyList<KeyValuePair<string, SkillParameterValue>> Parameters;
            internal Ability(GameplayAbilitySpec spec, int index)
            {
                Asset = DefinitionSource.Of(spec.Definition) as GameplayAbilityAsset;
                Name = Asset != null ? Asset.name : "能力 #" + (index + 1);
                Active = spec.IsActive; OnCooldown = spec.IsOnCooldown;
                TagGatePassed = spec.Definition.ActivationRequirements.Matches(spec.Owner.OwnedTags);
                Level = spec.IsActive ? spec.EvaluationContext?.Level ?? spec.Level : spec.Level; CooldownRemaining = spec.CooldownRemaining;
                Failure = spec.LastActivationCheck;
                var parameters = new List<KeyValuePair<string, SkillParameterValue>>();
                spec.EvaluationContext?.Parameters.CopyTo(parameters); Parameters = parameters.AsReadOnly();
                CooldownDuration = spec.CooldownDuration;
            }
        }
        public sealed class Animation
        {
            public readonly GameplayAbilityAsset AbilityAsset;
            public readonly SkillAnimationAsset Asset;
            public readonly SkillAnimationDefinition Definition;
            public readonly string AbilityName, SectionId, QueuedSectionId;
            public readonly ClipWeight[] Weights;
            public readonly float Duration, NormalizedTime, AuthoredTime;
            public bool Loop => Definition.Loop;
            internal Animation(AnimationTask task)
            {
                Definition = task.Definition; SectionId=task.SectionId; QueuedSectionId=task.QueuedSectionId;
                var weights=new ClipWeight[8]; int count=task.CopyWeights(weights); Array.Resize(ref weights,count); Weights=weights;
                AbilityAsset = DefinitionSource.Of(task.Ability.Definition) as GameplayAbilityAsset;
                Asset = DefinitionSource.Of(Definition) as SkillAnimationAsset;
                AbilityName = AbilityAsset != null ? AbilityAsset.name : Definition.ClipName;
                Duration = task.Duration; NormalizedTime = task.NormalizedTime;
                AuthoredTime = NormalizedTime * Definition.Duration;
            }
        }
        public readonly struct Tag
        {
            public readonly GameplayTag Value;
            public readonly int Count;
            public readonly bool IsNew;
            public readonly double AddedAt;
            internal Tag(GameplayTag value, int count, bool added, double addedAt)
            { Value = value; Count = count; IsNew = added; AddedAt = addedAt; }
        }
        public readonly struct Attribute
        {
            public readonly AttributeId Id;
            public readonly string Name;
            public readonly float BaseValue, CurrentValue;
            public float Delta => CurrentValue - BaseValue;
            internal Attribute(GameplayAttribute value)
            { Id = value.Id; Name = AttributeCatalog.Name(Id.Value); BaseValue = value.BaseValue; CurrentValue = value.CurrentValue; }
        }
        public readonly struct Effect
        {
            public readonly GameplayEffectAsset Asset;
            public readonly ActiveGameplayEffectHandle Handle;
            public readonly string Name, SourceName;
            public readonly EffectDurationPolicy Policy;
            public readonly float Remaining, Duration, Period;
            public readonly int Stacks;
            internal Effect(ActiveGameplayEffectInfo info, Func<object, string> unitName)
            {
                Asset = DefinitionSource.Of(info.Definition) as GameplayEffectAsset;
                Handle = info.Handle; Name = Asset != null ? Asset.name : "效果 #" + info.Handle;
                if (Asset != null && AssetDatabase.IsSubAsset(Asset) &&
                    AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(Asset)) is GameplayAbilityAsset ability)
                    Name = ability.name + " / " + Name;
                object source = info.Source is AbilitySourceIdentity identity ? identity.Owner : info.Source;
                SourceName = source == null && info.Source is AbilitySourceIdentity ? "来源已回收" : unitName?.Invoke(source) ?? "未知来源";
                Policy = info.Definition.DurationPolicy; Remaining = info.RemainingTime;
                Duration = info.Duration; Period = info.Period; Stacks = info.StackCount;
            }
        }

        public static RuntimeSnapshot Capture(AbilitySystemComponent owner, Func<object, string> unitName,
            RuntimeSnapshot previous = null, double capturedAt = 0)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return new RuntimeSnapshot(owner, unitName, previous, capturedAt);
        }

        private RuntimeSnapshot(AbilitySystemComponent owner, Func<object, string> unitName, RuntimeSnapshot previous, double capturedAt)
        {
            Owner = owner; UnitName = unitName?.Invoke(owner) ?? "未命名单位"; CapturedAt = capturedAt;
            var abilities = new List<Ability>(owner.GrantedAbilityCount);
            for (int i = 0; i < owner.GrantedAbilityCount; i++) abilities.Add(new Ability(owner.GetGrantedAbility(i), i));
            Abilities = abilities.AsReadOnly();
            var tasks = new List<Animation>();
            for (int i = 0; i < owner.ActiveTaskCount; i++)
                if (owner.GetActiveTask(i) is AnimationTask task && task.IsActive) tasks.Add(new Animation(task));
            Tasks = tasks.AsReadOnly();
            var actions = new List<ActionState>();
            for (int i = 0; i < owner.ActiveTaskCount; i++)
                if (owner.GetActiveTask(i) is ISkillTaskState state && owner.GetActiveTask(i).IsActive) actions.Add(new ActionState(state, false));
            var detached = new List<GameplayAbilityTask>(); SceneSkillTaskHost.CopyTasks(detached);
            foreach (var task in detached)
                if (task.IsActive && task is ISkillTaskState state && ReferenceEquals(state.Execution.Ability.Owner, owner)) actions.Add(new ActionState(state, true));
            Actions = actions.AsReadOnly();
            var tags = new List<Tag>();
            var oldTags = new Dictionary<GameplayTag, Tag>();
            if (previous != null && ReferenceEquals(previous.Owner, owner))
                foreach (var tag in previous.Tags) oldTags[tag.Value] = tag;
            foreach (var tag in owner.OwnedTags.GetExplicitTags())
            {
                bool existed = oldTags.TryGetValue(tag, out var old);
                tags.Add(new Tag(tag, owner.OwnedTags.GetTagCount(tag), !existed, existed ? old.AddedAt : capturedAt));
            }
            tags.Sort((a, b) => string.CompareOrdinal(a.Value.Name, b.Value.Name)); Tags = tags.AsReadOnly();
            var values = new List<GameplayAttribute>(owner.Attributes.Count); owner.Attributes.CopyTo(values);
            values.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            var attributes = new List<Attribute>(values.Count);
            foreach (var value in values) attributes.Add(new Attribute(value));
            Attributes = attributes.AsReadOnly();
            var effects = new List<Effect>(owner.Effects.Count);
            for (int i = 0; i < owner.Effects.Count; i++) effects.Add(new Effect(owner.Effects[i], unitName));
            Effects = effects.AsReadOnly();
        }
    }
}
