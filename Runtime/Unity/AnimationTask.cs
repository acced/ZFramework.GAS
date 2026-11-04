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
        private TimelineTaskContext _taskContext;
        private EffectEvaluationContext _effectValues;
        private AbilitySourceIdentity _sourceIdentity;
        private readonly Boundary[] _boundaries;
        private int _nextBoundary;
        private readonly float _timeScale;
        private readonly struct Boundary
        {
            /// <summary>构造完成后是本次播放的实际秒数（已缩放并夹到 <see cref="Duration"/>）。</summary>
            public readonly float Time;
            public readonly float AuthoredTime;
            public readonly int Kind, Index;
            public Boundary(float time, int kind, int index, float? authoredTime = null) { Time = time; AuthoredTime = authoredTime ?? time; Kind = kind; Index = index; }
        }

        private float _cycleTime;
        private int _section = -1, _jumpVersion;
        private string _queuedSection;
        private readonly IRuntimeTrackHandler[] _trackHandlers;
        private readonly bool[] _trackOpen;
        private readonly bool[] _trackDone;
        public string QueuedSectionId => _queuedSection;
        public int CopyWeights(Span<ClipWeight> into) => _driver is IAnimationDebugInfo debug ? debug.CopyWeights(into) : 0;
        public string SectionId => _section >= 0 ? _definition.Sections[_section].Id : null;



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
            _taskContext = taskContext?.Snapshot();
            _trackHandlers = new IRuntimeTrackHandler[definition.RuntimeTracks.Count]; _trackOpen = new bool[_trackHandlers.Length]; _trackDone = new bool[_trackHandlers.Length];
            if (definition.TaskEvents.Length > 0 && taskContext == null)
                throw new ArgumentNullException(nameof(taskContext), "Task events require host scene services.");
            _boundaries = new Boundary[definition.Markers.Length + definition.CueRegions.Length * 2 + definition.TagWindows.Length * 2 + definition.TaskEvents.Length + _trackHandlers.Length * 2];
            int b = 0;
            for (int i = 0; i < _trackHandlers.Length; i++)
            { _boundaries[b++] = new Boundary(definition.RuntimeTracks[i].Start, 6, i); _boundaries[b++] = new Boundary(definition.RuntimeTracks[i].End, 7, i); }
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

            if(driver is IBoneWeightDriver bones) bones.SetNextBoneWeights(definition.BoneWeights);
            if (driver is ISkillLayerDriver layered) layered.ConfigureNextLayer(definition.Mask, definition.Additive, definition.LayerWeight);
            string playClip = SkillAnimator.ResolveClip(driver, definition.ClipName, taskContext?.Execution?.Asset?.clipOverrides);
            float played = driver is AnimatorAnimationDriver animator && definition.Sections.Count > 0
                ? animator.PlayAt(playClip, durationOverride, definition.Loop, definition.BlendIn, definition.Sections[0].Start / definition.Duration)
                : driver is IAdvancedBlendingDriver advanced
                ? advanced.Play(playClip, durationOverride, definition.Loop, definition.BlendInSpec)
                : driver is IBlendingAnimationDriver blending
                ? blending.Play(playClip, durationOverride, definition.Loop, definition.BlendIn)
                : driver.Play(playClip, durationOverride, definition.Loop);

            if(played>0)SkillAnimator.RecordExit(driver,definition.BlendOutSpec);

            // 驱动找不到片段时返回 0：表现缺失不应连带把判定一起丢掉，
            // 时间轴按调用方要求的时长（没有则按授权时长）照常推进。
            Duration = played > 0f ? played
                : durationOverride > 0f ? durationOverride
                : definition.Duration;

            // 实际时长 ÷ 授权时长。所有时间点都按它缩放，且只算这一次。
            _timeScale = Duration / definition.Duration;

            // authored × scale 在浮点下可能比 Duration 多出一个 ulp
            // （如授权 0.8s 拉到 0.44s），圈末 head 恰好等于 Duration，多出的那一点会让
            // 「随动画结束」的区间与窗口收不掉，循环到下一圈再开一次就泄漏了计数。
            // 夹到 Duration 保持单调，排好的顺序不变。
            for (int i = 0; i < _boundaries.Length; i++)
            {
                Boundary authored = _boundaries[i];
                _boundaries[i] = new Boundary(ToActual(authored.Time), authored.Kind, authored.Index, authored.Time);
            }
        }

        /// <summary>本次播放的实际时长（秒）。</summary>
        public float Duration { get; }

        /// <summary>本次任务实际持有的定义；资产在编辑器中修改后该实例仍然有效。</summary>
        public SkillAnimationDefinition Definition => _definition;

        /// <summary>当前这一圈的归一化进度（0~1）。</summary>
        public float NormalizedTime => _cycleTime / Duration;
        private float ToActual(float authored) => Math.Min(authored * _timeScale, Duration);

        protected override void OnBegin()
        {
            _sourceIdentity = Ability.Owner.SourceIdentity;
            _effectValues = _taskContext?.Execution?.Values ?? Ability.EvaluationContext ?? new EffectEvaluationContext(Ability.Level);
            bool configured = _trackHandlers.Length > 0 || _definition.TaskEvents.Length > 0;
            foreach (var marker in _definition.Markers) configured |= marker.Actions.Count > 0;
            if (configured && _taskContext?.Execution == null)
            {
                var execution = new SkillExecutionContext(DefinitionSource.Of(Ability.Definition) as GameplayAbilityAsset, Ability, _target, _effectValues, _taskContext);
                _taskContext = execution.Services;
            }
            if (_taskContext?.Execution!=null && _driver is ISkillRigService rig) _taskContext.Execution.Services.Rig??=rig;
            for (int i = 0; i < _trackHandlers.Length; i++) _trackHandlers[i] = _definition.RuntimeTracks[i].Create(_taskContext.Execution);
            if (_definition.Sections.Count > 0) JumpToSection(_definition.Sections[0].Id);
        }

        public bool QueueSection(string id)
        {
            if (!IsActive || _section < 0 || FindSection(id) < 0) return false;
            var section = _definition.Sections[_section];
            if (_cycleTime < ToActual(section.InputStart) || _cycleTime > ToActual(section.InputEnd)) return false;
            _queuedSection = id; return true;
        }
        private int FindSection(string id)
        { for (int i = 0; i < _definition.Sections.Count; i++) if (_definition.Sections[i].Id == id) return i; return -1; }
        public bool JumpToSection(string id)
        {
            int index = FindSection(id); if (!IsActive || index < 0) return false;
            bool jumping = _section >= 0;
            float destination = ToActual(_definition.Sections[index].Start);
            int consumed = destination == _cycleTime ? _nextBoundary : 0;
            int version = ++_jumpVersion;
            CloseTimelineState(true);
            if (!IsActive || version != _jumpVersion) return false;
            _section = index; _queuedSection = null;
            float time = _definition.Sections[index].Start;
            for (int i = 0; i < _trackDone.Length; i++) _trackDone[i] = ToActual(_definition.RuntimeTracks[i].End) <= destination;
            _cycleTime = destination;
            _nextBoundary = 0;
            while (_nextBoundary < _boundaries.Length && _boundaries[_nextBoundary].Time < _cycleTime) _nextBoundary++;
            _nextBoundary = Math.Max(_nextBoundary, consumed);
            // Restore only windows covering the destination. Never execute skipped markers.
            for (int i = 0; i < _definition.TagWindows.Length; i++)
            {
                var window = _definition.TagWindows[i];
                if ((ToActual(window.Start) < destination || WasConsumed(0, i)) && (window.End < 0 || ToActual(window.End) > destination))
                    for (int j = 0; j < window.Tags.Count; j++)
                    {
                        _windowTagsAdded[i]++; Ability.Owner.OwnedTags.AddTag(window.Tags[j]);
                        if (!IsActive || version != _jumpVersion) return false;
                    }
            }
            for (int i = 0; i < _definition.CueRegions.Length && IsActive; i++)
            {
                var region = _definition.CueRegions[i];
                if ((ToActual(region.Start) < destination || WasConsumed(1, i)) && (region.End < 0 || ToActual(region.End) > destination))
                {
                    _regionOpen[i] = true; DispatchCue(region.CueTag, GameplayCueEvent.OnActive);
                    if (!IsActive || version != _jumpVersion) return false;
                }
            }
            if (!IsActive || version != _jumpVersion) return false;
            var transition = _definition.Sections[index].Transition;
            transition.duration *= _timeScale;
            if (!(jumping && transition.duration > 0 && _driver is IAdvancedBlendingDriver advanced &&
                (advanced.Capabilities & AnimationBlendCapabilities.SeekBlend) != 0 && advanced.SeekBlend(NormalizedTime, transition)))
            {
                if (!jumping && _driver is PlayableAnimationDriver playable) playable.SetEntryTime(NormalizedTime);
                else if ((jumping || _driver is not AnimatorAnimationDriver) && _driver is ISeekableAnimationDriver seekable) seekable.Seek(NormalizedTime);
                else _driver.Advance(NormalizedTime);
            }
            if (!IsActive || version != _jumpVersion) return false;
            SampleTracks(time);
            return IsActive && version == _jumpVersion;
            bool WasConsumed(int kind, int item)
            {
                for (int b = 0; b < consumed; b++) if (_boundaries[b].Kind == kind && _boundaries[b].Index == item) return true;
                return false;
            }
        }

        protected override void OnTick(float deltaTime)
        {
            try
            {
                float scale = HitStopLease.Scale(Ability.Owner,_taskContext?.Time);
                if (!(scale >= 0) || !float.IsFinite(scale)) throw new InvalidOperationException("局部动画速度必须是有限非负数。");
                if (_driver is ISkillAnimationSpeed speed) speed.SetLocalScale(scale);
                Tick(deltaTime * scale);
            }
            catch (Exception exception) { _taskContext?.Execution?.Fail(exception, _taskContext.Execution.CurrentActionId); throw; }
        }
        private void Tick(float deltaTime)
        {
            if (_section >= 0) { TickSections(deltaTime); return; }
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
                SampleTracks(head / Duration * _definition.Duration);
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
                Array.Clear(_trackDone, 0, _trackDone.Length);
                _nextBoundary = 0;
            }
        }

        private void TickSections(float deltaTime)
        {
            int transitions = 0;
            while (IsActive)
            {
                if (++transitions > 1024) throw new InvalidOperationException("单帧 Section 切换超过 1024 次。");
                var section = _definition.Sections[_section]; float end = ToActual(section.End);
                float remaining = Math.Max(0, end - _cycleTime);
                float step = Math.Min(deltaTime, remaining); _cycleTime = deltaTime >= remaining ? end : _cycleTime + step; deltaTime -= step;
                bool atEnd = _cycleTime == end;
                // Sections own [start, end). Decide the successor before admitting shared-end
                // events or entering tracks there (a loop must never hit the next section).
                if (!ProcessBoundaries(_cycleTime, inclusive: !atEnd)) return;
                int version = _jumpVersion;
                _driver.Advance(NormalizedTime);
                if (!IsActive || version != _jumpVersion) return;
                SampleTracks(atEnd ? section.End : NormalizedTime * _definition.Duration, includeStarts: !atEnd);
                if (!IsActive || version != _jumpVersion || _cycleTime < end) return;
                string next = _queuedSection ?? section.Next;
                if (string.IsNullOrEmpty(next)) { Complete(); return; }
                int nextIndex = FindSection(next);
                if (nextIndex >= 0 && _definition.Sections[nextIndex].Start == section.End)
                {
                    _section = nextIndex; _queuedSection = null;
                    if (!ProcessBoundaries(end)) return;
                    SampleTracks(section.End);
                }
                else if (!JumpToSection(next)) return;
                if (deltaTime <= 0) return;
            }
        }
        private void SampleTracks(float time, bool includeStarts = true)
        {
            int version = _jumpVersion;
            for (int i = 0; i < _trackHandlers.Length && IsActive; i++)
            {
                var definition = _definition.RuntimeTracks[i];
                if (!_trackOpen[i] && !_trackDone[i] && (time > definition.Start || includeStarts && time == definition.Start))
                { _trackOpen[i] = true; _trackHandlers[i].Enter(); }
                if (!IsActive || version != _jumpVersion) return;
                if (!_trackOpen[i] || !IsActive) continue;
                _trackHandlers[i].Sample(Math.Min(definition.End - definition.Start, Math.Max(0, time - definition.Start)), UnityEngine.Mathf.InverseLerp(definition.Start, definition.End, time));
                if (!IsActive || version != _jumpVersion) return;
                if (time >= definition.End) { _trackOpen[i] = false; _trackDone[i] = true; _trackHandlers[i].Exit(); }
                if (!IsActive || version != _jumpVersion) return;
            }
        }
        private void CloseTimelineState(bool cancelled)
        {
            int version = _jumpVersion;
            Exception error = null;
            for (int i = 0; version == _jumpVersion && i < _windowTagsAdded.Length; i++)
                while (version == _jumpVersion && _windowTagsAdded[i] > 0)
                { int tag = --_windowTagsAdded[i]; try { Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[i].Tags[tag]); } catch (Exception ex) { error ??= ex; } }
            for (int i = 0; version == _jumpVersion && i < _regionOpen.Length; i++) if (_regionOpen[i])
            { _regionOpen[i] = false; try { DispatchCue(_definition.CueRegions[i].CueTag, GameplayCueEvent.Removed); } catch (Exception ex) { error ??= ex; } }
            for (int i = 0; version == _jumpVersion && i < _trackHandlers.Length; i++) if (_trackOpen[i])
            {
                _trackOpen[i] = false;
                try { if (cancelled) _trackHandlers[i].Cancel(); else _trackHandlers[i].Exit(); } catch (Exception ex) { error ??= ex; }
            }
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        protected override void OnEnd(bool cancelled)
        {
            Exception error = null;
            try { CloseTimelineState(cancelled); } catch (Exception ex) { error = ex; }
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

            try
            {
                if (_driver is IAdvancedBlendingDriver advanced) advanced.Stop(_definition.BlendOutSpec);
                else if (_driver is IBlendingAnimationDriver blending) blending.Stop(_definition.BlendOut);
                else _driver.Stop();
            }
            catch (Exception ex) { error ??= ex; }

            // 放在最后：回调里多半要结束能力，届时区间已收尾、动画已停。
            try { _onFinished?.Invoke(this); } catch (Exception ex) { error ??= ex; }
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        // Merge boundaries once at construction. Large ticks still observe the correct window state
        // at every marker; same-time opens precede events and closes follow events.
        private bool ProcessBoundaries(float head, bool inclusive = true)
        {
            int version = _jumpVersion;
            while (_nextBoundary < _boundaries.Length && (_boundaries[_nextBoundary].Time < head || inclusive && _boundaries[_nextBoundary].Time == head))
            {
                Boundary b = _boundaries[_nextBoundary++];
                SampleTracks(b.AuthoredTime);
                if (!IsActive || version != _jumpVersion) return false;
                switch (b.Kind)
                {
                    case 0:
                        var tags = _definition.TagWindows[b.Index].Tags;
                        for (int i = _windowTagsAdded[b.Index]; i < tags.Count; i++)
                        {
                            // Commit ownership before notification: cancellation can reenter OnEnd.
                            _windowTagsAdded[b.Index]++;
                            Ability.Owner.OwnedTags.AddTag(tags[i]);
                            if (!IsActive || version != _jumpVersion) return false;
                        }
                        break;
                    case 1:
                        if (!_regionOpen[b.Index])
                        {
                            _regionOpen[b.Index] = true;
                            DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.OnActive);
                        }
                        break;
                    case 2:
                        var marker = _definition.Markers[b.Index];
                        SkillActionDebug.Marker(_taskContext?.Execution,marker);
                        if (!IsActive || version != _jumpVersion) return false;
                        if (marker.Effect != null)
                        {
                            var owner = Ability.Owner;
                            var receiver = marker.Target == AnimationMarkerTarget.Self ? owner : _target;
                            if (_taskContext?.Execution != null) _taskContext.Execution.ExecuteAction(marker.LegacyAction);
                            else receiver.Effects.Apply(new GameplayEffectSpec(marker.Effect, owner.Attributes, _sourceIdentity, context: _effectValues));
                            if (!IsActive || version != _jumpVersion) return false;
                        }
                        if (marker.Actions != null && marker.Actions.Count > 0)
                        {
                            var execution = _taskContext?.Execution ?? throw new InvalidOperationException("Configured actions require SkillRunner execution context.");
                            execution.Execute(marker.Actions);
                            if (!IsActive || version != _jumpVersion) return false;
                        }
                        _onMarker?.Invoke(this, marker);
                        break;
                    case 3:
                        var taskEvent = _definition.TaskEvents[b.Index];
                        if (taskEvent.Action != null && _taskContext?.Execution != null)
                        {
                            _taskContext.Execution.ExecuteAction(taskEvent.Action);
                        }
                        else taskEvent.Spawner.Spawn(Ability, _taskContext);
                        break;
                    case 4:
                        while (_windowTagsAdded[b.Index] > 0)
                        {
                            int index = --_windowTagsAdded[b.Index];
                            Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[b.Index].Tags[index]);
                            if (!IsActive || version != _jumpVersion) return false;
                        }
                        break;
                    case 5:
                        if (_regionOpen[b.Index])
                        {
                            _regionOpen[b.Index] = false;
                            DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.Removed);
                        }
                        break;
                }
                if (!IsActive || version != _jumpVersion) return false;
            }
            return true;
        }

        private void DispatchCue(GameplayTag cueTag, GameplayCueEvent cueEvent)
        {
            AbilitySystemComponent owner = Ability.Owner;
            owner.CueRouter?.Dispatch(new GameplayCueParameters(cueTag, cueEvent, owner, _sourceIdentity, 1));
        }
    }
}
