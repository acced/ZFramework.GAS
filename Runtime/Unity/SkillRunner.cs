using System;
using System.Collections.Generic;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// Per-owner asset-based casting facade. Assets remain shared data; specs belong to this runner's ASC.
    /// Skills end after their owned work completes. Scene-owned work does not block another cast.
    /// Looping skills require Cancel. The host still advances the ASC and supplies scene services.
    /// </summary>
    public sealed class SkillRunner
    {
        private readonly AbilitySystemComponent _owner;
        private readonly Func<IAnimationDriver> _getDriver;
        private readonly Dictionary<GameplayAbilityAsset, Entry> _skills = new();
        private readonly object _directGrant = new();
        private readonly object _implicitGrant = new();
        private long _grantOrder;
        private sealed class GrantSettings { public int Level; public SkillParameterOverride[] Overrides; public long Order; }
        private sealed class Entry
        {
            public GameplayAbilitySpec Spec;
            public Action<AnimationTask, AnimationMarker> Marker;
            public SkillParameterOverride[] Overrides;
            public SkillExecutionContext Execution;
            public readonly Dictionary<object, GrantSettings> Grants = new();
        }

        public event Action<GameplayAbilityAsset, AnimationMarker> MarkerReached;
        /// <summary>Outcome of the last synchronous TryCast, including failures before an asset could be granted.</summary>
        public AbilityActivationCheck LastCastCheck { get; private set; }

        public SkillRunner(AbilitySystemComponent owner, Func<IAnimationDriver> getDriver)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _getDriver = getDriver;
        }

        /// <summary>Grant once per asset and owner. Re-grants after the host clears/removes the underlying spec.</summary>
        public void Grant(GameplayAbilityAsset asset, int level = 1, SkillParameterOverride[] overrides = null)
            => GrantFrom(_directGrant, asset, level, overrides);
        private void GrantFrom(object source, GameplayAbilityAsset asset, int level, SkillParameterOverride[] overrides)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var entry = Find(asset);
            if (entry == null) entry = new Entry
            {
                Marker = (_, marker) => MarkerReached?.Invoke(asset, marker)
            };
            EnsureSpec(asset, entry);
            _skills[asset] = entry;
            if (!ReferenceEquals(source, _implicitGrant)) entry.Grants.Remove(_implicitGrant);
            entry.Grants[source] = new GrantSettings { Level = Math.Max(1, level), Overrides = CloneOverrides(overrides), Order = ++_grantOrder };
            RefreshGrant(entry);
        }
        private static SkillParameterOverride[] CloneOverrides(SkillParameterOverride[] values)
        {
            if (values == null) return null;
            var copy = (SkillParameterOverride[])values.Clone();
            for (int i = 0; i < copy.Length; i++) copy[i].levels = values[i].levels == null ? null : (float[])values[i].levels.Clone();
            return copy;
        }
        private static void RefreshGrant(Entry entry)
        {
            GrantSettings selected = null;
            foreach (var grant in entry.Grants.Values) if (selected == null || grant.Order > selected.Order) selected = grant;
            if (selected != null) { if (entry.Spec != null) entry.Spec.Level = selected.Level; entry.Overrides = selected.Overrides; }
        }
        private void EnsureSpec(GameplayAbilityAsset asset, Entry entry)
        {
            if (entry.Spec != null) return;
            entry.Spec = _owner.GrantAbility(asset.Definition);
            RefreshGrant(entry);
        }
        internal void GrantSet(AbilitySetAsset source)
        {
            var desired = new HashSet<GameplayAbilityAsset>();
            foreach (var item in source.abilities)
            {
                if (item?.skill == null || !desired.Add(item.skill)) throw new InvalidOperationException(source.name + ": 技能套组包含缺失或重复技能。");
                _ = item.skill.Definition; item.skill.ResolveParameters(item.level, item.parameters);
            }
            foreach (var item in source.abilities) GrantFrom(source, item.skill, item.level, item.parameters);
            RevokeSource(source, desired);
        }
        internal void RevokeSource(object source, HashSet<GameplayAbilityAsset> keep = null)
        {
            var assets = new List<GameplayAbilityAsset>(_skills.Keys);
            foreach (var asset in assets)
            {
                var entry = Find(asset);
                if (entry == null || keep != null && keep.Contains(asset) || !entry.Grants.Remove(source)) continue;
                if (entry.Grants.Count == 0) RevokeAll(asset); else RefreshGrant(entry);
            }
        }

        /// <summary>
        /// Grants on first use. Invalid animation configuration is rejected before activation commits cost/cooldown.
        /// If driver startup fails after activation, the ability is ended; committed cost/cooldown are not refunded.
        /// </summary>
        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, AbilitySystemComponent target = null,
            TimelineTaskContext taskContext = null, float durationOverride = 0, SkillParameterOverride[] parameterOverrides = null)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            Entry entry = null;
            SkillAnimationDefinition animation = null;
            IAnimationDriver driver = null;
            SkillExecutionContext execution;
            SkillActionDefinition[] actions;
            try
            {
                if (Find(asset) == null) GrantFrom(_implicitGrant, asset, 1, null);
                entry = _skills[asset];
                EnsureSpec(asset, entry);
                if (entry.Spec.IsActive)
                {
                    LastCastCheck = _owner.CheckActivation(entry.Spec);
                    return LastCastCheck.Result;
                }
                if (!ReferenceEquals(entry.Spec.Definition, asset.Definition)) _owner.UpdateAbilityDefinition(entry.Spec, asset.Definition);
                animation = asset.Animation != null ? asset.Animation.Definition : null;
                if (animation != null)
                {
                    if (animation.RequiresTarget && target == null) throw new ArgumentNullException(nameof(target));
                    if (animation.TaskEvents.Length > 0 && taskContext == null) throw new ArgumentNullException(nameof(taskContext));
                    if (float.IsNaN(durationOverride) || float.IsInfinity(durationOverride)) throw new ArgumentOutOfRangeException(nameof(durationOverride));
                    driver = _getDriver?.Invoke() ?? throw new InvalidOperationException("Animated skills require an animation driver.");
                }
                var parameters = asset.ResolveParameters(entry.Spec.Level, MergeOverrides(entry.Overrides, parameterOverrides));
                execution = new SkillExecutionContext(asset, entry.Spec, target,
                    new EffectEvaluationContext(entry.Spec.Level, parameters, _owner.Attributes), taskContext);
                actions = asset.BuildActions();
                SkillActionBuildContext.Validate(actions, execution);
                if (animation != null) foreach (var marker in animation.Markers)
                {
                    if (marker.Effect != null) SkillActionBuildContext.Effect(marker.Effect, execution, target?.Attributes);
                    SkillActionBuildContext.Validate(marker.Actions, execution);
                }
                if (animation != null) foreach (var task in animation.TaskEvents) task.Action?.Validate(execution);
            }
            catch (Exception exception)
            {
                LastCastCheck = AbilityActivationCheck.PreflightFailure(exception, (exception as SkillActionException)?.ActionId);
                if (entry?.Spec != null) entry.Spec.LastExecutionFailure = LastCastCheck;
                return LastCastCheck.Result;
            }
            var spec = execution.Ability;
            var result = _owner.TryActivateAbility(spec, execution.Values);
            LastCastCheck = spec.LastActivationCheck;
            if (result != AbilityActivationResult.Activated) return result;
            entry.Execution = execution;
            execution.BindActivation();
            entry.Spec.LastExecutionFailure = default;
            entry.Spec.LastCleanupError = null; entry.Spec.LastCleanupActionId = null;
            try
            {
                execution.Execute(actions);
                if (!execution.IsCastActive)
                {
                    if (spec.ActivationVersion == execution.Version && spec.LastExecutionFailure.Result == AbilityActivationResult.ExecutionFailed)
                    { LastCastCheck = spec.LastExecutionFailure; return LastCastCheck.Result; }
                    return result;
                }
                // The observer runs after the animation in reverse task order, allowing same-frame completion.
                var observer = new ExecutionLifetimeTask(execution);
                _owner.StartTask(entry.Spec, observer);
                if (animation != null)
                {
                    var task = new AnimationTask(animation, driver, target, durationOverride, entry.Marker, null, execution.Services);
                    _owner.StartTask(entry.Spec, task);
                }
                observer.FinishIfIdle();
            }
            catch (Exception exception)
            {
                execution.Fail(exception, execution.CurrentActionId);
                try { if (spec.ActivationVersion == execution.Version) driver?.Stop(); }
                catch (Exception cleanup) { _owner.RecordCleanupError(spec, execution.Version, cleanup, execution.CurrentActionId); }
                LastCastCheck = spec.ActivationVersion == execution.Version && spec.LastExecutionFailure.Error != null
                    ? spec.LastExecutionFailure : AbilityActivationCheck.FromError(AbilityActivationResult.ExecutionFailed, exception, execution.CurrentActionId);
                return LastCastCheck.Result;
            }
            return result;
        }

        private sealed class ExecutionLifetimeTask : GameplayAbilityTask, ISkillTaskState
        {
            public SkillExecutionContext Execution { get; }
            public string ActionId => Execution.CurrentActionId;
            public string State => Execution.Status;
            public ExecutionLifetimeTask(SkillExecutionContext execution) { Execution = execution; }
            protected override void OnTick(float deltaTime) => FinishIfIdle();
            public void FinishIfIdle()
            {
                if (!IsActive) return;
                var owner = Ability.Owner;
                for (int i = 0; i < owner.ActiveTaskCount; i++)
                {
                    var task = owner.GetActiveTask(i);
                    if (!ReferenceEquals(task, this) && task.IsActive && ReferenceEquals(task.Ability, Ability)) return;
                }
                Execution.Status = "完成";
                Complete(); owner.EndAbility(Ability);
            }
            protected override void OnEnd(bool cancelled)
            {
                if (!cancelled || Execution.Error != null) return;
                var failure = Ability.LastExecutionFailure;
                if (failure.Result == AbilityActivationResult.ExecutionFailed)
                    Execution.Fail(failure.Error ?? new InvalidOperationException(failure.Message), failure.SourceId, alreadyRecorded: true);
                else Execution.Status = "已取消";
            }
        }

        private static SkillParameterOverride[] MergeOverrides(SkillParameterOverride[] granted, SkillParameterOverride[] cast)
        {
            if (granted == null || granted.Length == 0) return cast;
            if (cast == null || cast.Length == 0) return granted;
            var result = new SkillParameterOverride[granted.Length + cast.Length];
            Array.Copy(granted, result, granted.Length); Array.Copy(cast, 0, result, granted.Length, cast.Length); return result;
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
            bool removed = entry.Grants.Remove(_directGrant) | entry.Grants.Remove(_implicitGrant);
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
            for (int i = 0; i < _owner.GrantedAbilityCount; i++)
                if (ReferenceEquals(_owner.GetGrantedAbility(i), entry.Spec)) return entry;
            // Readback/revocation must not recreate specs. Retain logical provenance until the
            // next Grant/TryCast explicitly restores runtime state after Clear/recycling.
            entry.Spec = null;
            entry.Execution = null;
            return entry;
        }
    }
}
