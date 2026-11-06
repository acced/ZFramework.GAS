using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS.Unity
{
    /// <summary>Explicit grants, cached definitions/parameters and per-cast state. Host owns ticking and error handling.</summary>
    public sealed class SkillRunner
    {
        private readonly AbilitySystemComponent _owner;
        private readonly Func<IAnimationDriver> _getDriver;
        private readonly SkillUnitServices _services;
        private readonly Dictionary<GameplayAbilityAsset, Entry> _skills = new();
        private readonly object _directGrant = new();
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
            public AnimationTask Timeline;
            public ConditionalWeakTable<SkillParameterOverride[], Prepared> Casts;
        }
        private sealed class Entry
        {
            public GameplayAbilitySpec Spec;
            public Action<AnimationTask, AnimationMarker> Marker;
            public SkillParameterOverride[] Overrides;
            public SkillExecutionContext Execution;
            public SkillActionDefinition[] Actions;
            public SkillAnimationDefinition Animation;
            public readonly List<GrantSettings> Grants = new();
            public Prepared Default;
        }
        public event Action<GameplayAbilityAsset, AnimationMarker> MarkerReached;
        public AbilityActivationCheck LastCastCheck { get; private set; }
        public SkillRunner(AbilitySystemComponent owner, Func<IAnimationDriver> getDriver)
            : this(owner, getDriver, null) { }
        public SkillRunner(AbilitySystemComponent owner, Func<IAnimationDriver> getDriver, SkillUnitServices services)
        { _owner = owner; _getDriver = getDriver; _services = services; }

        public void Grant(GameplayAbilityAsset asset, int level=1, SkillParameterOverride[] overrides=null)
            => GrantFrom(_directGrant,asset,level,overrides);
        private void GrantFrom(object source,GameplayAbilityAsset asset,int level,SkillParameterOverride[] overrides)
        {
            if(!_skills.TryGetValue(asset,out var entry)){
                entry=new Entry{Marker=(_,marker)=>MarkerReached?.Invoke(asset,marker)};
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
            if(entry.Spec==null){entry.Default=null;entry.Spec=_owner.GrantAbility(definition);RefreshGrant(entry);}
            if(!ReferenceEquals(entry.Spec.Definition,definition)){
                _owner.UpdateAbilityDefinition(entry.Spec,definition);entry.Default=null;
            }
            entry.Actions=asset.BuildActions();
            entry.Animation=asset.Animation!=null?asset.Animation.Definition:null;
        }
        private static Prepared Prepare(GameplayAbilityAsset asset,Entry entry,SkillParameterOverride[] cast)
        {
            int level = entry.Spec.Level;
            Prepared Resolve(SkillParameterOverride[] overrides) => new Prepared {
                Values = new EffectEvaluationContext(level, asset.ResolveParameters(level, entry.Overrides, overrides))
            };
            if (entry.Default == null || entry.Default.Values.Level != level) entry.Default = Resolve(null);
            if (cast == null || cast.Length == 0) return entry.Default;

            // Reused override arrays stay warm without the runner retaining one-off arrays.
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
                var entry=Find(asset);
                if(keep!=null && keep.Contains(asset) || entry.Grants.RemoveAll(g=>ReferenceEquals(g.Source,source))==0)continue;
                if(entry.Grants.Count==0)RevokeAll(asset);else RefreshGrant(entry);
            }
        }
        /// <summary>Grant explicitly before casting. Override arrays are immutable cache keys; replace the array to change values.</summary>
        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, AbilitySystemComponent target=null,
            TimelineTaskContext taskContext=null,float durationOverride=0,SkillParameterOverride[] parameterOverrides=null)
            => Cast(asset, SkillCastTarget.From(target, taskContext), taskContext ?? _services, durationOverride, parameterOverrides);

        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, SkillCastTarget target,
            SkillParameterOverride[] parameterOverrides=null, float durationOverride=0)
            => Cast(asset, target, _services, durationOverride, parameterOverrides);

        private AbilityActivationResult Cast(GameplayAbilityAsset asset, SkillCastTarget target, SkillUnitServices services,
            float durationOverride, SkillParameterOverride[] parameterOverrides)
        {
            var entry=Find(asset) ?? throw new InvalidOperationException("技能未授予: "+asset.name);
            if(entry.Spec?.IsActive==true){LastCastCheck=_owner.CheckActivation(entry.Spec);return LastCastCheck.Result;}
            EnsureSpec(asset,entry);
            var prepared=Prepare(asset,entry,parameterOverrides);
            var spec=entry.Spec;
            var result=_owner.TryActivateAbility(spec,prepared.Values);LastCastCheck=spec.LastActivationCheck;
            if(result!=AbilityActivationResult.Activated)return result;
            var execution=prepared.Context;
            if(execution==null || !execution.Reusable) {
                prepared.Context=execution=new SkillExecutionContext(asset,spec,target,prepared.Values,services);
                prepared.Lifetime=new ExecutionLifetimeTask(execution);prepared.Timeline=null;
            }
            else execution.Reset(target,services);
            entry.Execution=execution;execution.BindActivation();execution.Retain();
            try{
                _owner.StartTask(spec, prepared.Lifetime);
                execution.Execute(entry.Actions);
                if(!execution.IsCastActive)return result;
                if(entry.Animation!=null){
                    var driver=_getDriver();
                    if(prepared.Timeline==null || prepared.Timeline.Definition!=entry.Animation)
                        prepared.Timeline=new AnimationTask(entry.Animation,driver,target.Unit,durationOverride,entry.Marker,null,execution.Services);
                    else prepared.Timeline.Replay(driver,target.Unit,durationOverride,execution.Services);
                    _owner.StartTask(spec,prepared.Timeline);
                }
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
        public bool QueueSection(GameplayAbilityAsset asset, string sectionId)
        {
            var spec = GetSpec(asset);
            for (int i = 0; i < _owner.ActiveTaskCount; i++)
                if (_owner.GetActiveTask(i) is AnimationTask task && ReferenceEquals(task.Ability, spec)) return task.QueueSection(sectionId);
            return false;
        }

        public bool IsActive(GameplayAbilityAsset asset) => Find(asset)?.Spec?.IsActive ?? false;
        public float CooldownRemaining(GameplayAbilityAsset asset) => Find(asset)?.Spec?.CooldownRemaining ?? 0;
        public float CooldownDuration(GameplayAbilityAsset asset) => Find(asset)?.Spec?.CooldownDuration ?? 0;
        public bool Cancel(GameplayAbilityAsset asset) => _owner.EndAbility(Find(asset)?.Spec);
        public bool Revoke(GameplayAbilityAsset asset)
        {
            var entry = Find(asset);
            if (entry == null) return false;
            bool removed = entry.Grants.RemoveAll(g => ReferenceEquals(g.Source,_directGrant)) != 0;
            if (!removed) return false;
            if (entry.Grants.Count == 0) RevokeAll(asset); else RefreshGrant(entry);
            return true;
        }
        /// <summary>Explicitly remove every grant, including ability-set contributions.</summary>
        public bool RevokeAll(GameplayAbilityAsset asset)
        {
            var entry = Find(asset);
            if (entry == null) return false;
            _skills.Remove(asset);
            _owner.RemoveAbility(entry.Spec); return true;
        }
        private Entry Find(GameplayAbilityAsset asset)
        {
            if (asset == null || !_skills.TryGetValue(asset, out var entry)) return null;
            if(entry.Spec?.IsGranted==true)return entry;
            // Readback/revocation must not recreate specs. Retain logical provenance until the
            // next Grant/TryCast explicitly restores runtime state after Clear/recycling.
            entry.Spec = null;
            entry.Execution = null;
            return entry;
        }
    }
}
