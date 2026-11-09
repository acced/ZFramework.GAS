using System;
using System.Collections.Generic;

namespace ZFramework.GAS.Unity
{
    public static class SkillAnimationBinding
    {
        public static SkillAnimationAsset Animation(this GameplayAbilityAsset ability) => ability.Presentation as SkillAnimationAsset;
    }

    /// <summary>Bind once, cast through SkillRunner, dispose before releasing the unit.</summary>
    public sealed class SkillTimelinePlayer : IDisposable
    {
        private readonly SkillRunner _runner;
        private readonly IAnimationDriver _driver;
        private readonly Dictionary<GameplayAbilityAsset, AnimationTask> _timelines = new();
        private AnimationTask _playing;
        public event Action<GameplayAbilityAsset, AnimationMarker> MarkerReached;

        public SkillTimelinePlayer(SkillRunner runner, IAnimationDriver driver = null)
        {
            _runner = runner; _driver = driver;
            _runner.CastStarted += Play;
        }

        private void Play(SkillExecutionContext execution)
        {
            if (!execution.IsCastActive) return;
            var asset = execution.Asset;
            var animation = asset.Animation();
            if (animation == null) return;
            var definition = animation.Definition;
            if (!execution.IsCastActive) return;
            var driver = definition.Clip != null ? _driver : null;
            if (_timelines.TryGetValue(asset, out var timeline) && timeline.Definition == definition && timeline.CanReplay)
                timeline.Replay(driver, execution);
            else
                _timelines[asset] = timeline = new AnimationTask(definition, driver, execution,
                    (_, marker) => MarkerReached?.Invoke(asset, marker));
            if (driver != null)
            {
                if (_playing != timeline) _playing?.DetachDriver();
                _playing = timeline;
            }
            execution.Ability.Owner.StartTask(execution.Ability, timeline);
        }

        public void Dispose()
        {
            _runner.CastStarted -= Play;
            foreach (var timeline in _timelines.Values)
                if (timeline.IsActive) timeline.Ability.Owner.EndAbility(timeline.Ability);
            _timelines.Clear();
            _driver?.Dispose();
        }
    }
}
