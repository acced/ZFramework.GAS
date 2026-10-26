using GameplayTags;
using System;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 按时间轴推进一段技能动画：到点施加效果、区间起止派发 Cue、驱动底层动画。
    ///
    /// <b>时钟权威在 gameplay 侧</b>：任务在能力的 tick 上推进，同时把时长下达给
    /// <see cref="IAnimationDriver"/>，两者按同一个时长走。因此不查询播放进度，也就不会漂移。
    ///
    /// 与所有 <see cref="GameplayAbilityTask"/> 一样，存活期是所属能力存活期的子集：
    /// 能力被打断时动画立即 <see cref="IAnimationDriver.Stop"/>，已开的 Cue 区间收尾。
    /// </summary>
    public sealed class AnimationTask : GameplayAbilityTask
    {
        private static readonly bool[] NoRegions = Array.Empty<bool>();

        private readonly SkillAnimationDefinition _definition;
        private readonly IAnimationDriver _driver;
        private readonly AbilitySystemComponent _target;
        private readonly Action<AnimationTask, AnimationMarker> _onMarker;
        private readonly Action<AnimationTask> _onFinished;
        private readonly bool[] _regionOpen;

        /// <summary>实际时长 ÷ 授权时长。标记点与区间的时间都按它缩放。</summary>
        private readonly float _scale;

        private float _cycleTime;
        private int _nextMarker;
        private int _nextRegion;

        /// <summary>
        /// 构造即开播——与 <see cref="ProjectileTask"/> 在构造时定下起点同源，
        /// 紧接着交给 <see cref="AbilitySystemComponent.StartTask"/>。
        /// </summary>
        /// <param name="target">
        /// 固定目标。定义里存在「作用于固定目标」的效果标记点时必须给出。
        /// </param>
        /// <param name="durationOverride">
        /// 大于 0 时把动画拉伸到该秒数（按攻速缩放攻击动作即用此参数）；
        /// 小于等于 0 时用动画的自然时长。
        /// </param>
        /// <param name="onMarker">
        /// 每个标记点到点时回调，用 <see cref="AnimationMarker.Key"/> 分辨是哪一下。
        /// 效果之外的事（范围查询、生成投射物、播音效）在这里做。
        /// </param>
        /// <param name="onFinished">
        /// 播完或被打断时回调（<see cref="GameplayAbilityTask.WasCancelled"/> 区分两者）。
        /// 「动画播完则技能结束」是使用方的决定而非本任务的——收招后还有后摇的技能不该被替它结束掉，
        /// 因此这里只报告，不代为调用 <see cref="AbilitySystemComponent.EndAbility"/>。
        /// </param>
        public AnimationTask(
            SkillAnimationDefinition definition,
            IAnimationDriver driver,
            AbilitySystemComponent target = null,
            float durationOverride = 0f,
            Action<AnimationTask, AnimationMarker> onMarker = null,
            Action<AnimationTask> onFinished = null)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));

            if (definition.RequiresTarget && target == null)
            {
                throw new ArgumentNullException(nameof(target),
                    "The animation has a marker that applies an effect to a fixed target, but no target was given.");
            }

            _target = target;
            _onMarker = onMarker;
            _onFinished = onFinished;
            _regionOpen = definition.CueRegions.Length > 0 ? new bool[definition.CueRegions.Length] : NoRegions;

            float played = driver.Play(definition.ClipName, durationOverride, definition.Loop);

            // 驱动找不到片段时返回 0：表现缺失不应连带把判定一起丢掉，
            // 时间轴按调用方要求的时长（没有则按授权时长）照常推进。
            Duration = played > 0f ? played
                : durationOverride > 0f ? durationOverride
                : definition.Duration;
            _scale = Duration / definition.Duration;
        }

        /// <summary>本次播放的实际时长（秒）。</summary>
        public float Duration { get; }

        /// <summary>当前这一圈的归一化进度（0~1）。</summary>
        public float NormalizedTime => _cycleTime / Duration;

        protected override void OnTick(float deltaTime)
        {
            _cycleTime += deltaTime;

            while (true)
            {
                // 单帧跨过整圈时，本轮只结算到圈末，余下的时间留给下一圈——
                // 否则同一圈里靠后的标记点会被跳过。
                float head = _cycleTime < Duration ? _cycleTime : Duration;

                if (!FireMarkers(head) || !OpenRegions(head) || !CloseRegions(head))
                {
                    // 效果连锁取消了所属能力，任务已随之结束。
                    return;
                }

                _driver.Advance(head / Duration);

                if (_cycleTime < Duration)
                {
                    return;
                }

                if (!_definition.Loop)
                {
                    Complete();
                    return;
                }

                // 循环动画不自行结束，由能力结束它。每圈重新触发标记点与区间。
                _cycleTime -= Duration;
                _nextMarker = 0;
                _nextRegion = 0;
            }
        }

        protected override void OnEnd(bool cancelled)
        {
            // 被打断时把已开的区间收尾，否则持续特效会永远挂着。
            for (int i = 0; i < _regionOpen.Length; i++)
            {
                if (_regionOpen[i])
                {
                    _regionOpen[i] = false;
                    DispatchCue(_definition.CueRegions[i].CueTag, GameplayCueEvent.Removed);
                }
            }

            _driver.Stop();

            // 放在最后：回调里多半要结束能力，届时区间已收尾、动画已停。
            _onFinished?.Invoke(this);
        }

        /// <summary>返回 false 表示任务已在回调中结束，调用方应立刻停手。</summary>
        private bool FireMarkers(float head)
        {
            AnimationMarker[] markers = _definition.Markers;
            while (_nextMarker < markers.Length)
            {
                int index = _nextMarker;
                ref readonly AnimationMarker marker = ref markers[index];
                if (marker.Time * _scale > head)
                {
                    return true;
                }

                // 先推进游标再触发：回调里若结束能力，重入时不会再看到这个标记点。
                _nextMarker = index + 1;

                if (marker.Effect != null)
                {
                    AbilitySystemComponent owner = Ability.Owner;
                    AbilitySystemComponent receiver = marker.Target == AnimationMarkerTarget.Self ? owner : _target;
                    receiver.Effects.Apply(new GameplayEffectSpec(marker.Effect, owner.Attributes, owner));
                    if (!IsActive)
                    {
                        return false;
                    }
                }

                _onMarker?.Invoke(this, marker);
                if (!IsActive)
                {
                    return false;
                }
            }

            return true;
        }

        private bool OpenRegions(float head)
        {
            AnimationCueRegion[] regions = _definition.CueRegions;
            while (_nextRegion < regions.Length)
            {
                int index = _nextRegion;
                if (regions[index].Start * _scale > head)
                {
                    return true;
                }

                _nextRegion = index + 1;
                _regionOpen[index] = true;
                DispatchCue(regions[index].CueTag, GameplayCueEvent.OnActive);
                if (!IsActive)
                {
                    return false;
                }
            }

            return true;
        }

        private bool CloseRegions(float head)
        {
            AnimationCueRegion[] regions = _definition.CueRegions;
            for (int i = 0; i < regions.Length; i++)
            {
                if (!_regionOpen[i])
                {
                    continue;
                }

                // End 为负表示随动画结束；head 在圈末恰好等于 Duration，因此区间一定收得掉。
                float end = regions[i].End < 0f ? Duration : regions[i].End * _scale;
                if (end > head)
                {
                    continue;
                }

                _regionOpen[i] = false;
                DispatchCue(regions[i].CueTag, GameplayCueEvent.Removed);
                if (!IsActive)
                {
                    return false;
                }
            }

            return true;
        }

        private void DispatchCue(GameplayTag cueTag, GameplayCueEvent cueEvent)
        {
            AbilitySystemComponent owner = Ability.Owner;
            owner.CueRouter?.Dispatch(new GameplayCueParameters(cueTag, cueEvent, owner, owner, 1));
        }
    }
}
