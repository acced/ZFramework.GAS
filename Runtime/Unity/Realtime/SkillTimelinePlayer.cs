using System;
using System.Collections.Generic;

namespace ZFramework.GAS.Unity
{
    public static class SkillAnimationBinding
    {
        public static SkillAnimationAsset Animation(this GameplayAbilityAsset ability) => ability.Presentation as SkillAnimationAsset;
    }

    public sealed class SkillTimelinePlayer : IDisposable
    {
        private readonly SkillRunner _runner;
        private readonly IAnimationDriver _driver;
        private readonly Dictionary<GameplayAbilityAsset, AnimationTask> _timelines = new();
        private AnimationTask _playing;

        public IAnimationDriver Driver => _driver;
        public AnimationTask ActiveTask => _playing != null && _playing.IsActive ? _playing : null;
        public event Action<GameplayAbilityAsset, AnimationMarker> MarkerReached;

        public SkillTimelinePlayer(SkillRunner runner, IAnimationDriver driver = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _driver = driver;
            _runner.CastStarted += Play;
            _runner.Revoked += OnRevoked;
        }

        private void Play(SkillExecutionContext execution)
        {
            if (!execution.IsCastActive) return;
            GameplayAbilityAsset asset = execution.Asset;
            SkillAnimationAsset animation = asset.Animation();
            if (animation == null) return;
            SkillAnimationDefinition def = animation.Definition;
            if (!execution.IsCastActive) return;

            IAnimationDriver driver = def.Clip != null ? _driver : null;
            if (_timelines.TryGetValue(asset, out AnimationTask timeline) && timeline.Definition == def && timeline.CanReplay)
                timeline.Replay(driver, execution);
            else
                _timelines[asset] = timeline = new AnimationTask(def, driver, execution, (_, m) => MarkerReached?.Invoke(asset, m));

            if (driver != null)
            {
                if (_playing != timeline) _playing?.DetachDriver();
                _playing = timeline;
            }
            execution.Ability.Owner.StartTask(execution.Ability, timeline);
        }

        private void OnRevoked(GameplayAbilityAsset asset)
        {
            if (_timelines.Remove(asset, out AnimationTask timeline) && ReferenceEquals(_playing, timeline)) _playing = null;
        }

        public void Dispose()
        {
            _runner.CastStarted -= Play;
            _runner.Revoked -= OnRevoked;
            foreach (AnimationTask t in _timelines.Values)
                if (t.IsActive) t.Ability.Owner.EndAbility(t.Ability, true);
            _timelines.Clear();
            _playing = null;
            _driver?.Dispose();
        }
    }
}
