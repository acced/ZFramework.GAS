using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS.Unity
{
    /// <summary>Explicit grants and one cast entry. The host owns ticking and task cancellation.</summary>
    public sealed class SkillRunner
    {
        private readonly AbilitySystemComponent _owner;
        private readonly SkillUnitServices _services;
        private readonly Dictionary<GameplayAbilityAsset, Entry> _skills = new();
        private sealed class Prepared
        {
            public EffectEvaluationContext Values;
            public SkillExecutionContext Context;
            public ExecutionLifetimeTask Lifetime;
            public ConditionalWeakTable<EffectEvaluationContext, Prepared> Casts;
        }
        private sealed class Entry
        {
            public GameplayAbilitySpec Spec;
            public SkillActionDefinition[] Actions;
            public bool AutoCommit;
            public Prepared Default;
        }
        public event Action<SkillExecutionContext> CastStarted;
        public SkillRunner(AbilitySystemComponent owner, SkillUnitServices services = null)
        { _owner = owner; _services = services; }

        public GameplayAbilitySpec Grant(GameplayAbilityAsset asset, int level = 1)
        {
            if (!_skills.TryGetValue(asset, out var entry)) _skills.Add(asset, entry = new Entry());
            Refresh(asset, entry);
            entry.Spec.Level = level;
            entry.Default = null;
            Prepare(asset, entry, null);
            return entry.Spec;
        }
        private void Refresh(GameplayAbilityAsset asset, Entry entry)
        {
            var definition = asset.Definition;
            var actions = asset.BuildActions();
            if (entry.Spec?.IsGranted != true) entry.Spec = _owner.GrantAbility(definition);
            else if (!ReferenceEquals(entry.Spec.Definition, definition)) _owner.UpdateAbilityDefinition(entry.Spec, definition);
            if (!ReferenceEquals(entry.Actions, actions)) entry.Default = null;
            entry.Actions = actions;
            entry.AutoCommit = asset.autoCommit;
        }
        private static Prepared Prepare(GameplayAbilityAsset asset, Entry entry, EffectEvaluationContext values)
        {
            int level = entry.Spec.Level;
            if (entry.Default == null || entry.Default.Values.Level != level)
                entry.Default = new Prepared { Values = new EffectEvaluationContext(level, asset.ResolveParameters(level)) };
            if (values == null) return entry.Default;
            var casts = entry.Default.Casts ??= new ConditionalWeakTable<EffectEvaluationContext, Prepared>();
            if (!casts.TryGetValue(values, out var prepared)) casts.Add(values, prepared = new Prepared { Values = values });
            return prepared;
        }

        public AbilityActivationCheck TryCast(GameplayAbilityAsset asset, SkillCastTarget target = default,
            EffectEvaluationContext values = null)
        {
            var entry = _skills[asset];
            if (entry.Spec.IsActive || !entry.Spec.IsGranted) return _owner.CheckActivation(entry.Spec);
#if UNITY_EDITOR
            Refresh(asset, entry);
#endif
            var prepared = Prepare(asset, entry, values);
            var spec = entry.Spec;
            var result = _owner.TryActivateAbility(spec, prepared.Values, entry.AutoCommit);
            if (!result.Succeeded) return result;
            var execution = prepared.Context;
            if (execution == null || !execution.Reusable)
            {
                prepared.Context = execution = new SkillExecutionContext(asset, spec, target, prepared.Values, _services);
                prepared.Lifetime = new ExecutionLifetimeTask(execution);
            }
            else execution.Reset(target, _services);
            execution.BindActivation(); execution.Retain();
            try
            {
                _owner.StartTask(spec, prepared.Lifetime);
                execution.Execute(entry.Actions);
                if (execution.IsCastActive) CastStarted?.Invoke(execution);
                prepared.Lifetime.FinishIfIdle();
                return result;
            }
            finally { execution.Release(); }
        }
        private sealed class ExecutionLifetimeTask : GameplayAbilityTask, ISkillTaskState
        {
            public SkillExecutionContext Execution { get; }
            public string ActionId => Execution.CurrentActionId;
            public string State => Execution.Status;
            public ExecutionLifetimeTask(SkillExecutionContext execution) => Execution = execution;
            protected override void OnTick(float deltaTime) => FinishIfIdle();
            internal void FinishIfIdle()
            {
                if (!IsActive) return;
                for (int i = 0; i < Ability.Owner.ActiveTaskCount; i++)
                {
                    var task = Ability.Owner.GetActiveTask(i);
                    if (!ReferenceEquals(task, this) && task.IsActive && ReferenceEquals(task.Ability, Ability)) return;
                }
                Execution.Status = "完成"; Complete(); Ability.Owner.EndAbility(Ability);
            }
            protected override void OnEnd(bool cancelled) { if (cancelled) Execution.Status = "已取消"; }
        }

        public GameplayAbilitySpec GetSpec(GameplayAbilityAsset asset)
            => _skills.TryGetValue(asset, out var entry) && entry.Spec.IsGranted ? entry.Spec : null;
        public bool Revoke(GameplayAbilityAsset asset)
        {
            if (!_skills.Remove(asset, out var entry)) return false;
            _owner.RemoveAbility(entry.Spec);
            return true;
        }
    }
}
