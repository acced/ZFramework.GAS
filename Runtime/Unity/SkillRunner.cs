using System;
using System.Collections.Generic;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// Per-owner asset-based casting facade. Assets remain shared data; specs belong to this runner's ASC.
    /// Animated skills end when their timeline finishes; skills without animation end immediately.
    /// Looping skills require Cancel. The host still advances the ASC and supplies scene services.
    /// </summary>
    public sealed class SkillRunner
    {
        private readonly AbilitySystemComponent _owner;
        private readonly Func<IAnimationDriver> _getDriver;
        private readonly Dictionary<GameplayAbilityAsset, Entry> _skills = new();
        private sealed class Entry
        {
            public GameplayAbilitySpec Spec;
            public Action<AnimationTask, AnimationMarker> Marker;
        }

        public event Action<GameplayAbilityAsset, AnimationMarker> MarkerReached;

        public SkillRunner(AbilitySystemComponent owner, Func<IAnimationDriver> getDriver)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _getDriver = getDriver;
        }

        /// <summary>Grant once per asset and owner. Re-grants after the host clears/removes the underlying spec.</summary>
        public void Grant(GameplayAbilityAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (Find(asset) != null) return;
            _skills[asset] = new Entry
            {
                Spec = _owner.GrantAbility(asset.Definition),
                Marker = (_, marker) => MarkerReached?.Invoke(asset, marker)
            };
        }

        /// <summary>
        /// Grants on first use. Invalid animation configuration is rejected before activation commits cost/cooldown.
        /// If driver startup fails after activation, the ability is ended; committed cost/cooldown are not refunded.
        /// </summary>
        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, AbilitySystemComponent target = null,
            TimelineTaskContext taskContext = null, float durationOverride = 0)
        {
            Grant(asset);
            var entry = _skills[asset];
            if (entry.Spec.IsActive) return AbilityActivationResult.AlreadyActive;
            var animation = asset.Animation != null ? asset.Animation.Definition : null;
            IAnimationDriver driver = null;
            if (animation != null)
            {
                if (animation.RequiresTarget && target == null) throw new ArgumentNullException(nameof(target));
                if (animation.TaskEvents.Length > 0 && taskContext == null) throw new ArgumentNullException(nameof(taskContext));
                if (float.IsNaN(durationOverride) || float.IsInfinity(durationOverride)) throw new ArgumentOutOfRangeException(nameof(durationOverride));
                driver = _getDriver?.Invoke() ?? throw new InvalidOperationException("Animated skills require an animation driver.");
            }
            var result = _owner.TryActivateAbility(entry.Spec);
            if (result != AbilityActivationResult.Activated) return result;
            if (animation == null) { _owner.EndAbility(entry.Spec); return result; }
            try
            {
                var task = new AnimationTask(animation, driver, target, durationOverride, entry.Marker, Finish, taskContext);
                _owner.StartTask(entry.Spec, task);
            }
            catch
            {
                try { driver.Stop(); }
                finally { _owner.EndAbility(entry.Spec); }
                throw;
            }
            return result;
        }

        private static void Finish(AnimationTask task)
        {
            // Cancellation already belongs to the ASC; do not end a reactivated ability from an old callback.
            if (!task.WasCancelled) task.Ability.Owner.EndAbility(task.Ability);
        }

        public bool IsActive(GameplayAbilityAsset asset) => Find(asset)?.Spec.IsActive ?? false;
        public float CooldownRemaining(GameplayAbilityAsset asset) => Find(asset)?.Spec.CooldownRemaining ?? 0;
        public float CooldownDuration(GameplayAbilityAsset asset) => Find(asset)?.Spec.Definition.Cooldown?.Duration ?? 0;
        public bool Cancel(GameplayAbilityAsset asset) => _owner.EndAbility(Find(asset)?.Spec);
        public bool Revoke(GameplayAbilityAsset asset)
        {
            var entry = Find(asset);
            if (entry == null) return false;
            _skills.Remove(asset);
            return _owner.RemoveAbility(entry.Spec);
        }
        private Entry Find(GameplayAbilityAsset asset)
        {
            if (asset == null || !_skills.TryGetValue(asset, out var entry)) return null;
            for (int i = 0; i < _owner.GrantedAbilityCount; i++)
                if (ReferenceEquals(_owner.GetGrantedAbility(i), entry.Spec)) return entry;
            _skills.Remove(asset);
            return null;
        }
    }
}
