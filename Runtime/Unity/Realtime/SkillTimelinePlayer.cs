using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ZFramework.GAS.Unity
{
    /// <summary>Realtime 扩展对能力表现槽的解释：技能动画与片段替换表。</summary>
    public static class SkillAnimationBinding
    {
        public static SkillAnimationAsset Animation(this GameplayAbilityAsset ability) => ability.Presentation as SkillAnimationAsset;
        public static SkillClipOverrideSet ClipOverrides(this GameplayAbilityAsset ability) => ability.PresentationVariant as SkillClipOverrideSet;
    }

    /// <summary>
    /// 把技能动画接到 <see cref="SkillRunner"/> 上：施放成功且激活动作执行完仍在施放时，
    /// 启动该技能的 <see cref="AnimationTask"/>。不接这个播放器，技能照常结算，只是没有时间轴。
    /// </summary>
    public sealed class SkillTimelinePlayer : IDisposable
    {
        private readonly SkillRunner _runner;
        private readonly Func<IAnimationDriver> _getDriver;
        // 同一执行上下文被下一次施放复用时，时间轴随之复用；新上下文自然拿不到旧时间轴。
        private readonly ConditionalWeakTable<SkillExecutionContext, AnimationTask> _timelines = new();
        private readonly Dictionary<GameplayAbilityAsset, Action<AnimationTask, AnimationMarker>> _markers = new();
        private float _pendingDuration;

        public event Action<GameplayAbilityAsset, AnimationMarker> MarkerReached;

        public SkillTimelinePlayer(SkillRunner runner, Func<IAnimationDriver> getDriver)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _getDriver = getDriver;
            _runner.CastStarted += Play;
        }

        /// <summary>按指定实际时长施放（例如攻速）。时长只作用于这一次施放的时间轴。</summary>
        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, SkillCastTarget target, float durationOverride,
            SkillParameterOverride[] parameterOverrides = null)
        {
            _pendingDuration = durationOverride;
            try { return _runner.TryCast(asset, target, parameterOverrides); }
            finally { _pendingDuration = 0; }
        }

        public AbilityActivationResult TryCast(GameplayAbilityAsset asset, AbilitySystemComponent target, SkillExecutionServices taskContext,
            float durationOverride, SkillParameterOverride[] parameterOverrides = null)
        {
            _pendingDuration = durationOverride;
            try { return _runner.TryCast(asset, target, taskContext, parameterOverrides); }
            finally { _pendingDuration = 0; }
        }

        public bool QueueSection(GameplayAbilityAsset asset, string sectionId)
        {
            var spec = _runner.GetSpec(asset);
            if (spec == null) return false;
            var owner = spec.Owner;
            for (int i = 0; i < owner.ActiveTaskCount; i++)
                if (owner.GetActiveTask(i) is AnimationTask task && ReferenceEquals(task.Ability, spec)) return task.QueueSection(sectionId);
            return false;
        }

        private void Play(GameplayAbilityAsset asset, GameplayAbilitySpec spec, SkillExecutionContext execution, EffectEvaluationContext values)
        {
            var animation = asset.Animation();
            if (animation == null) return;
            var definition = animation.Definition;
            var driver = _getDriver?.Invoke();
            if (_timelines.TryGetValue(execution, out var timeline) && timeline.Definition == definition)
                timeline.Replay(driver, execution.Target, _pendingDuration, execution.Services);
            else
            {
                _timelines.Remove(execution);
                timeline = new AnimationTask(definition, driver, execution.Target, _pendingDuration, MarkerCallback(asset), null, execution.Services);
                _timelines.Add(execution, timeline);
            }
            spec.Owner.StartTask(spec, timeline);
        }

        private Action<AnimationTask, AnimationMarker> MarkerCallback(GameplayAbilityAsset asset)
        {
            if (!_markers.TryGetValue(asset, out var callback))
                _markers.Add(asset, callback = (_, marker) => MarkerReached?.Invoke(asset, marker));
            return callback;
        }

        public void Dispose() => _runner.CastStarted -= Play;
    }
}
