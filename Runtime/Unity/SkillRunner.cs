using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS.Unity
{
    /// <summary>Called after a successful activation to let the host start presentation (animation, VFX, etc.).</summary>
    public delegate void SkillCastCallback(GameplayAbilityAsset asset, GameplayAbilitySpec spec,
        SkillExecutionContext execution, EffectEvaluationContext values);

    /// <summary>Explicit grants, cached definitions/parameters and per-cast state. Host owns ticking and error handling.</summary>
    public sealed class SkillRunner
    {
        private readonly AbilitySystemComponent _owner;
        private readonly SkillUnitServices _services;
        private readonly Dictionary<GameplayAbilityAsset, Entry> _skills = new();
        private sealed class GrantSettings
        {
            public object Source;
            public int Level;
            public SkillParameterOverride[] Overrides;
        }
        private sealed class Prepared
        {
            public EffectEvaluationContext Values;
            public SkillExecutionContext Context;
            public ExecutionLifetimeTask Lifetime;
            public ConditionalWeakTable<SkillParameterOverride[], Prepared> Casts;
        }
        private sealed class Entry
        {
            public GameplayAbilitySpec Spec;
            public SkillParameterOverride[] Overrides;
            public SkillExecutionContext Execution;
            public SkillActionDefinition[] Actions;
            public bool AutoCommit;
            public readonly List<GrantSettings> Grants = new();
            public Prepared Default;
        }
        /// <summary>Invoked after a successful activation so the host can start animation / presentation.</summary>
        public event SkillCastCallback CastStarted;
        public AbilityActivationCheck LastCastCheck { get; private set; }
        public SkillRunner(AbilitySystemComponent owner, SkillUnitServices services = null)
        { _owner = owner; _services = services; }

        public void Grant(GameplayAbilityAsset asset, int level=1, SkillParameterOverride[] overrides=null)
            => GrantFrom(this,asset,level,overrides);
        private void GrantFrom(object source,GameplayAbilityAsset asset,int level,SkillParameterOverride[] overrides)
        {
            if(!_skills.TryGetValue(asset,out var entry)){
                entry=new Entry();
                _skills.Add(asset,entry);
            }
            entry.Grants.RemoveAll(g=>ReferenceEquals(g.Source,source));
            entry.Grants.Add(new GrantSettings{Source=source,Level=Math.Max(1,level),Overrides=overrides});
            EnsureSpec(asset,entry); RefreshGrant(entry); Prepare(asset,entry,null);
        }
        private static void RefreshGrant(Entry entry)
        {
            var latest=entry.Grants[entry.Grants.Count-1];
            if(entry.Spec!=null)entry.Spec.Level=latest.Level;
            if (entry.Overrides != latest.Overrides) entry.Default = null;
            entry.Overrides=latest.Overrides;
        }
        private void EnsureSpec(GameplayAbilityAsset asset,Entry entry)
        {
            var definition=asset.Definition;
            var actions=asset.BuildActions();
            if(entry.Spec?.IsGranted!=true){entry.Default=null;entry.Spec=_owner.GrantAbility(definition);RefreshGrant(entry);}
            if(!ReferenceEquals(entry.Spec.Definition,definition)){
                _owner.UpdateAbilityDefinition(entry.Spec,definition);entry.Default=null;
            }
            entry.Actions=actions;
            entry.AutoCommit=asset.autoCommit;
        }
        private static Prepared Prepare(GameplayAbilityAsset asset,Entry entry,SkillParameterOverride[] cast)
        {
            int level = entry.Spec.Level;
            Prepared Resolve(SkillParameterOverride[] overrides) => new Prepared {
                Values = new EffectEvaluationContext(level, asset.ResolveParameters(level, entry.Overrides, overrides))
            };
            if (entry.Default == null || entry.Default.Values.Level != level) entry.Default = Resolve(null);
            if (cast == null || cast.Length == 0) return entry.Default;

            var casts = entry.Default.Casts ??= new ConditionalWeakTable<SkillParameterOverride[], Prepared>();
            if (!casts.TryGetValue(cast, out var prepared)) casts.Add(cast, prepared = Resolve(cast));
            return prepared;
        }
        internal void GrantSet(AbilitySetAsset source)
        {
            var desired=new HashSet<GameplayAbilityAsset>();
            foreach(var item in source.abilities){
                if(item?.skill==null || !desired.Add(item.skill))throw new InvalidOperationException("技能套组包含缺失或重复技能。");
                GrantFrom(source,item.skill,item.level,item.parameters);
            }
            RevokeSource(source,desired);
        }
        internal void RevokeSource(object source,HashSet<GameplayAbilityAsset> keep=null)
        {
            var assets=new List<GameplayAbilityAsset>(_skills.Keys);
            foreach(var asset in assets){
                var entry=_skills[asset];
                if(keep!=null && keep.Contains(asset) || entry.Grants.RemoveAll(g=>ReferenceEquals(g.Source,source))==0)continue;
                if(entry.Grants.Count==0)RevokeAll(asset);else RefreshGrant(entry);
            }
        }
        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, AbilitySystemComponent target=null,
            SkillExecutionServices taskContext=null,SkillParameterOverride[] parameterOverrides=null)
            => Cast(asset, SkillCastTarget.From(target, taskContext), taskContext ?? _services, parameterOverrides);

        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, SkillCastTarget target,
            SkillParameterOverride[] parameterOverrides=null)
            => Cast(asset, target, _services, parameterOverrides);

        private AbilityActivationResult Cast(GameplayAbilityAsset asset, SkillCastTarget target, SkillUnitServices services,
            SkillParameterOverride[] parameterOverrides)
        {
            var entry=Find(asset) ?? throw new InvalidOperationException("技能未授予: "+asset.name);
            if(entry.Spec?.IsActive==true){LastCastCheck=_owner.CheckActivation(entry.Spec);return LastCastCheck.Result;}
#if UNITY_EDITOR
            EnsureSpec(asset,entry);
#endif
            var prepared=Prepare(asset,entry,parameterOverrides);
            var spec=entry.Spec;
            var result=_owner.TryActivateAbility(spec,prepared.Values,entry.AutoCommit);LastCastCheck=spec.LastActivationCheck;
            if(result!=AbilityActivationResult.Activated)return result;
            var execution=prepared.Context;
            if(execution==null || !execution.Reusable) {
                prepared.Context=execution=new SkillExecutionContext(asset,spec,target,prepared.Values,services);
                prepared.Lifetime=new ExecutionLifetimeTask(execution);
            }
            else execution.Reset(target,services);
            entry.Execution=execution;execution.BindActivation();execution.Retain();
            try{
                _owner.StartTask(spec, prepared.Lifetime);
                execution.Execute(entry.Actions);
                if(execution.IsCastActive) CastStarted?.Invoke(asset, spec, execution, prepared.Values);
                prepared.Lifetime.FinishIfIdle();
                return result;
            }
            finally{execution.Release();}
        }
        private sealed class ExecutionLifetimeTask : GameplayAbilityTask, ISkillTaskState
        {
            public SkillExecutionContext Execution {get;}
            public string ActionId=>Execution.CurrentActionId;
            public string State=>Execution.Status;
            public ExecutionLifetimeTask(SkillExecutionContext execution){Execution=execution;}
            protected override void OnTick(float deltaTime) => FinishIfIdle();
            internal void FinishIfIdle()
            {
                if (!IsActive) return;
                for(int i=0;i<Ability.Owner.ActiveTaskCount;i++){
                    var task=Ability.Owner.GetActiveTask(i);
                    if(!ReferenceEquals(task,this) && task.IsActive && ReferenceEquals(task.Ability,Ability))return;
                }
                Execution.Status="完成";Complete();Ability.Owner.EndAbility(Ability);
            }
            protected override void OnEnd(bool cancelled){if(cancelled)Execution.Status="已取消";}
        }

        public GameplayAbilitySpec GetSpec(GameplayAbilityAsset asset) => Find(asset)?.Spec;
        public SkillExecutionContext GetExecution(GameplayAbilityAsset asset) => Find(asset)?.Execution;

        public bool IsActive(GameplayAbilityAsset asset) => Find(asset)?.Spec?.IsActive ?? false;
        public float CooldownRemaining(GameplayAbilityAsset asset) => Find(asset)?.Spec?.CooldownRemaining ?? 0;
        public float CooldownDuration(GameplayAbilityAsset asset) => Find(asset)?.Spec?.CooldownDuration ?? 0;
        public bool Cancel(GameplayAbilityAsset asset) => _owner.EndAbility(Find(asset)?.Spec);
        public bool Revoke(GameplayAbilityAsset asset)
        {
            if (!_skills.TryGetValue(asset, out var entry)) return false;
            bool removed = entry.Grants.RemoveAll(g => ReferenceEquals(g.Source,this)) != 0;
            if (!removed) return false;
            if (entry.Grants.Count == 0) RevokeAll(asset); else RefreshGrant(entry);
            return true;
        }
        public bool RevokeAll(GameplayAbilityAsset asset)
        {
            if (!_skills.TryGetValue(asset, out var entry)) return false;
            _skills.Remove(asset);
            _owner.RemoveAbility(entry.Spec); return true;
        }
        private Entry Find(GameplayAbilityAsset asset)
            => _skills.TryGetValue(asset, out var entry) && entry.Spec?.IsGranted == true ? entry : null;
    }
}
