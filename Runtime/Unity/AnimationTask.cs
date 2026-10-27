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
        private readonly int[] _windowTagsAdded;
        private readonly TimelineTaskContext _taskContext;
        private readonly Boundary[] _boundaries;
        private int _nextBoundary;
        private readonly struct Boundary
        {
            /// <summary>构造完成后是本次播放的实际秒数（已缩放并夹到 <see cref="Duration"/>）。</summary>
            public readonly float Time;
            public readonly int Kind, Index;
            public Boundary(float time, int kind, int index) { Time = time; Kind = kind; Index = index; }
        }

        private float _cycleTime;



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
            Action<AnimationTask> onFinished = null,
            TimelineTaskContext taskContext = null)
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
            _windowTagsAdded = new int[definition.TagWindows.Length];
            _taskContext = taskContext;
            if (definition.TaskEvents.Length > 0 && taskContext == null)
                throw new ArgumentNullException(nameof(taskContext), "Task events require host scene services.");
            _boundaries = new Boundary[definition.Markers.Length + definition.CueRegions.Length * 2 + definition.TagWindows.Length * 2 + definition.TaskEvents.Length];
            int b = 0;
            for (int i = 0; i < definition.TagWindows.Length; i++)
            {
                var w = definition.TagWindows[i];
                _boundaries[b++] = new Boundary(w.Start, 0, i);
                _boundaries[b++] = new Boundary(w.End < 0 ? definition.Duration : w.End, 4, i);
            }
            for (int i = 0; i < definition.CueRegions.Length; i++)
            {
                var r = definition.CueRegions[i];
                _boundaries[b++] = new Boundary(r.Start, 1, i);
                _boundaries[b++] = new Boundary(r.End < 0 ? definition.Duration : r.End, 5, i);
            }
            for (int i = 0; i < definition.Markers.Length; i++) _boundaries[b++] = new Boundary(definition.Markers[i].Time, 2, i);
            for (int i = 0; i < definition.TaskEvents.Length; i++) _boundaries[b++] = new Boundary(definition.TaskEvents[i].Time, 3, i);
            Array.Sort(_boundaries, static (a, c) =>
            { int order = a.Time.CompareTo(c.Time); if (order == 0) order = a.Kind.CompareTo(c.Kind); return order == 0 ? a.Index.CompareTo(c.Index) : order; });

            float played = driver.Play(definition.ClipName, durationOverride, definition.Loop);

            // 驱动找不到片段时返回 0：表现缺失不应连带把判定一起丢掉，
            // 时间轴按调用方要求的时长（没有则按授权时长）照常推进。
            Duration = played > 0f ? played
                : durationOverride > 0f ? durationOverride
                : definition.Duration;

            // 实际时长 ÷ 授权时长。所有时间点都按它缩放，且只算这一次。
            float scale = Duration / definition.Duration;

            // authored × scale 在浮点下可能比 Duration 多出一个 ulp
            // （如授权 0.8s 拉到 0.44s），圈末 head 恰好等于 Duration，多出的那一点会让
            // 「随动画结束」的区间与窗口收不掉，循环到下一圈再开一次就泄漏了计数。
            // 夹到 Duration 保持单调，排好的顺序不变。
            for (int i = 0; i < _boundaries.Length; i++)
            {
                Boundary authored = _boundaries[i];
                _boundaries[i] = new Boundary(Math.Min(authored.Time * scale, Duration), authored.Kind, authored.Index);
            }
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

                if (!ProcessBoundaries(head))
                {
                    // 效果连锁取消了所属能力，任务已随之结束。
                    return;
                }

                _driver.Advance(head / Duration);
                if (!IsActive) return;

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


                _nextBoundary = 0;
            }
        }

        protected override void OnEnd(bool cancelled)
        {
            Exception error = null;
            for (int i = 0; i < _windowTagsAdded.Length; i++)
            {
                while (_windowTagsAdded[i] > 0)
                {
                    int tag = --_windowTagsAdded[i];
                    try { Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[i].Tags[tag]); }
                    catch (Exception ex) { error ??= ex; }
                }
            }
            // 被打断时把已开的区间收尾，否则持续特效会永远挂着。
            for (int i = 0; i < _regionOpen.Length; i++)
            {
                if (_regionOpen[i])
                {
                    _regionOpen[i] = false;
                    try { DispatchCue(_definition.CueRegions[i].CueTag, GameplayCueEvent.Removed); }
                    catch (Exception ex) { error ??= ex; }
                }
            }

            try { _driver.Stop(); } catch (Exception ex) { error ??= ex; }

            // 放在最后：回调里多半要结束能力，届时区间已收尾、动画已停。
            try { _onFinished?.Invoke(this); } catch (Exception ex) { error ??= ex; }
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        // Merge boundaries once at construction. Large ticks still observe the correct window state
        // at every marker; same-time opens precede events and closes follow events.
        private bool ProcessBoundaries(float head)
        {
            while (_nextBoundary < _boundaries.Length && _boundaries[_nextBoundary].Time <= head)
            {
                Boundary b = _boundaries[_nextBoundary++];
                switch (b.Kind)
                {
                    case 0:
                        var tags = _definition.TagWindows[b.Index].Tags;
                        for (int i = 0; i < tags.Count; i++)
                        {
                            // Commit ownership before notification: cancellation can reenter OnEnd.
                            _windowTagsAdded[b.Index] = i + 1;
                            Ability.Owner.OwnedTags.AddTag(tags[i]);
                            if (!IsActive) return false;
                        }
                        break;
                    case 1:
                        _regionOpen[b.Index] = true;
                        DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.OnActive);
                        break;
                    case 2:
                        var marker = _definition.Markers[b.Index];
                        if (marker.Effect != null)
                        {
                            var owner = Ability.Owner;
                            var receiver = marker.Target == AnimationMarkerTarget.Self ? owner : _target;
                            receiver.Effects.Apply(new GameplayEffectSpec(marker.Effect, owner.Attributes, owner));
                            if (!IsActive) return false;
                        }
                        _onMarker?.Invoke(this, marker);
                        break;
                    case 3:
                        _definition.TaskEvents[b.Index].Spawner.Spawn(Ability, _taskContext);
                        break;
                    case 4:
                        while (_windowTagsAdded[b.Index] > 0)
                        {
                            int index = --_windowTagsAdded[b.Index];
                            Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[b.Index].Tags[index]);
                            if (!IsActive) return false;
                        }
                        break;
                    case 5:
                        _regionOpen[b.Index] = false;
                        DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.Removed);
                        break;
                }
                if (!IsActive) return false;
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
