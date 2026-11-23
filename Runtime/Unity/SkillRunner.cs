using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS.Unity
{
    /// <summary>显式授予与唯一施放入口；宿主控制 Tick 与取消。</summary>
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

        public AbilitySystemComponent Owner => _owner;
        public SkillUnitServices Services => _services;
        public event Action<SkillExecutionContext> CastStarted;
        public event Action<GameplayAbilityAsset> Revoked;

        public SkillRunner(AbilitySystemComponent owner, SkillUnitServices services = null)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _services = services;
        }

        public GameplayAbilitySpec Grant(GameplayAbilityAsset asset, int level = 1)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (!_skills.TryGetValue(asset, out Entry entry)) _skills.Add(asset, entry = new Entry());
            Refresh(asset, entry);
            entry.Spec.Level = level;
            entry.Default = null;
            Prepare(asset, entry, null);
            return entry.Spec;
        }

        private void Refresh(GameplayAbilityAsset asset, Entry entry)
        {
            GameplayAbilityDefinition def = asset.Definition;
            SkillActionDefinition[] actions = asset.BuildActions();
            if (entry.Spec?.IsGranted != true) entry.Spec = _owner.GrantAbility(def);
            else if (!ReferenceEquals(entry.Spec.Definition, def)) _owner.UpdateAbilityDefinition(entry.Spec, def);
            if (!ReferenceEquals(entry.Actions, actions)) entry.Default = null;
            entry.Actions = actions;
            entry.AutoCommit = asset.autoCommit;
        }

        private static Prepared Prepare(GameplayAbilityAsset asset, Entry entry, EffectEvaluationContext values)
        {
            int level = Math.Max(1, entry.Spec.Level);
            if (entry.Default == null || entry.Default.Values.Level != level)
            {
                var def = new EffectEvaluationContext(level, asset.ResolveParameters(level));
                entry.Default = new Prepared { Values = def };
                entry.Spec.PreparedDefaultContext = def;
            }
            if (values == null) return entry.Default;
            var casts = entry.Default.Casts ??= new ConditionalWeakTable<EffectEvaluationContext, Prepared>();
            if (!casts.TryGetValue(values, out Prepared prepared)) casts.Add(values, prepared = new Prepared { Values = values });
            return prepared;
        }

        public AbilityActivationCheck CanCast(GameplayAbilityAsset asset, EffectEvaluationContext values = null)
        {
            if (asset == null || !_skills.TryGetValue(asset, out Entry entry))
                return new AbilityActivationCheck(AbilityActivationResult.Inactive, "技能未授予");
            if (entry.Spec.IsActive || !entry.Spec.IsGranted) return _owner.CheckActivation(entry.Spec);
#if UNITY_EDITOR
            Refresh(asset, entry);
#endif
            return _owner.CheckActivation(entry.Spec, Prepare(asset, entry, values).Values);
        }

        public AbilityActivationCheck TryCast(GameplayAbilityAsset asset, SkillCastTarget target = default, EffectEvaluationContext values = null)
        {
            Entry entry = _skills[asset];
            if (entry.Spec.IsActive || !entry.Spec.IsGranted) return _owner.CheckActivation(entry.Spec);
#if UNITY_EDITOR
            Refresh(asset, entry);
#endif
            Prepared prepared = Prepare(asset, entry, values);
            GameplayAbilitySpec spec = entry.Spec;
            AbilityActivationCheck result = _owner.TryActivateAbility(spec, prepared.Values, entry.AutoCommit);
            if (!result.Succeeded) return result;

            SkillExecutionContext execution = prepared.Context;
            ExecutionLifetimeTask lifetime = prepared.Lifetime;
            if (execution == null || !execution.Reusable)
            {
                prepared.Context = execution = new SkillExecutionContext(asset, spec, target, prepared.Values, _services);
                prepared.Lifetime = lifetime = new ExecutionLifetimeTask(execution);
            }
            else execution.Reset(target, _services);

            execution.BindActivation();
            execution.Retain();
            try
            {
                _owner.StartTask(spec, lifetime);
                execution.Execute(entry.Actions);
                if (execution.IsCastActive) CastStarted?.Invoke(execution);
                lifetime.FinishIfIdle();
                return result;
            }
            finally { execution.Release(); }
        }

        private sealed class ExecutionLifetimeTask : GameplayAbilityTask, ISkillTaskState
        {
            public SkillExecutionContext Execution { get; }
            public string ActionId => Execution.CurrentActionId;
            public string State => Execution.Status;
            public override string DiagnosticId => ActionId;

            public ExecutionLifetimeTask(SkillExecutionContext execution) => Execution = execution;
            protected override void OnTick(float deltaTime) => FinishIfIdle();

            internal void FinishIfIdle()
            {
                if (!IsActive) return;
                AbilitySystemComponent owner = Ability.Owner;
                for (int i = 0; i < owner.ActiveTaskCount; i++)
                {
                    GameplayAbilityTask t = owner.GetActiveTask(i);
                    if (!ReferenceEquals(t, this) && t.IsActive && ReferenceEquals(t.Ability, Ability)) return;
                }
                Execution.Status = "完成";
                Complete();
                owner.EndAbility(Ability);
            }

            protected override void OnEnd(bool cancelled) { if (cancelled) Execution.Status = "已取消"; }
        }

        public GameplayAbilitySpec GetSpec(GameplayAbilityAsset asset)
            => asset != null && _skills.TryGetValue(asset, out Entry entry) && entry.Spec.IsGranted ? entry.Spec : null;

        public bool Revoke(GameplayAbilityAsset asset)
        {
            if (asset == null || !_skills.Remove(asset, out Entry entry)) return false;
            _owner.RemoveAbility(entry.Spec);
            Revoked?.Invoke(asset);
            return true;
        }
    }
}
