using GameplayTags;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace ZFramework.GAS
{
    public enum EffectApplicationResult { Completed, Applied, Stacked, Refreshed, Overflowed, Blocked }

    /// <summary>目标身上的持续效果容器；外部只持 <see cref="ActiveGameplayEffectHandle"/>，内部对象延迟回收复用。</summary>
    public sealed class GameplayEffectContainer
    {
        private const int MaxChainDepth = 8;
        private const int MaxClearPasses = MaxChainDepth * 4;
        private static long s_nextHandle;

        private readonly AttributeSet _attributes;
        private readonly GameplayTagCountContainer _ownedTags;
        private readonly GameplayCueRouter _cueRouter;
        private readonly object _owner;
        private readonly List<ActiveGameplayEffect> _active = new();
        private readonly Stack<ActiveGameplayEffect> _pool = new();
        private int _opDepth, _chainDepth, _pendingRemoves;
        private bool _ticking, _clearing;

        public event Action<ActiveGameplayEffectInfo> OnEffectAdded;
        public event Action<ActiveGameplayEffectInfo> OnEffectRemoved;
        public event Action<ActiveGameplayEffectInfo> OnEffectStackChanged;

        public GameplayEffectContainer(AttributeSet attributes, GameplayTagCountContainer ownedTags,
            GameplayCueRouter cueRouter = null, object owner = null)
        {
            _attributes = attributes ?? throw new ArgumentNullException(nameof(attributes));
            _ownedTags = ownedTags ?? throw new ArgumentNullException(nameof(ownedTags));
            _cueRouter = cueRouter; _owner = owner;
        }

        public int Count => _active.Count - _pendingRemoves;

        public ActiveGameplayEffectInfo this[int index]
        {
            get
            {
                if (_pendingRemoves == 0) return CreateInfo(_active[index]);
                for (int i = 0; i < _active.Count; i++)
                    if (!_active[i].PendingRemove && index-- == 0) return CreateInfo(_active[i]);
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public EffectApplicationResult Apply(in GameplayEffectSpec spec) => Apply(spec, out _);

        public EffectApplicationResult Apply(in GameplayEffectSpec spec, out ActiveGameplayEffectHandle handle)
        {
            _opDepth++;
            try { return ApplyCore(spec, out handle, default, false, 0f, 0f); }
            finally { EndOp(); }
        }

        internal EffectApplicationResult ApplyResolved(in GameplayEffectSpec spec, ReadOnlySpan<float> magnitudes, float duration, float period)
        {
            _opDepth++;
            try { return ApplyCore(spec, out _, magnitudes, true, duration, period); }
            finally { EndOp(); }
        }

        private EffectApplicationResult ApplyCore(GameplayEffectSpec spec, out ActiveGameplayEffectHandle handle,
            ReadOnlySpan<float> magnitudes, bool resolved, float duration, float period)
        {
            handle = default;
            GameplayEffectDefinition def = spec.Definition;
            EffectTags tags = def.Tags;
            if (!resolved && !def.ApplicationRequirements.Matches(_ownedTags)) return EffectApplicationResult.Blocked;

            if (def.DurationPolicy == EffectDurationPolicy.Instant)
            {
                if (resolved) ApplyMagnitudes(def.Modifiers, magnitudes, 1);
                else ApplyInstant(spec, 1);
                GasDebugHooks.Apply(_owner, def, spec.Source, 1);
                RemoveEffectsWithAnyTag(tags.RemoveEffectsWith);
                DispatchCues(def, GameplayCueEvent.Executed, spec.Source, 1);
                ApplyConditional(spec, EffectTrigger.Applied);
                return EffectApplicationResult.Completed;
            }

            if (!resolved) { period = spec.ResolvePeriod(_attributes); duration = spec.ResolveDuration(_attributes); }
            if (!float.IsFinite(duration)) throw new InvalidOperationException($"Effect resolved to non-finite duration ({duration}).");
            if (duration < 0f) duration = 0f;
            if (!float.IsFinite(period) || period < 0f) throw new InvalidOperationException($"Effect resolved to invalid period ({period}).");

            ActiveGameplayEffect existing = FindStackable(spec);
            if (existing != null)
            {
                handle = existing.Handle;
                int limit = def.Stacking.Limit;
                if (limit > 0 && existing.StackCount >= limit)
                {
                    if (def.Stacking.Overflow == StackOverflowPolicy.RemoveExisting)
                    {
                        MarkForRemoval(existing); handle = default;
                        return EffectApplicationResult.Overflowed;
                    }
                    RefreshTimers(existing);
                    GasDebugHooks.Refresh(_owner, def, spec.Source, existing.StackCount);
                    return EffectApplicationResult.Refreshed;
                }
                existing.StackCount++;
                PushStackCount(existing);
                RefreshTimers(existing);
                GasDebugHooks.Stack(_owner, def, spec.Source, existing.StackCount);
                OnEffectStackChanged?.Invoke(CreateInfo(existing));
                return EffectApplicationResult.Stacked;
            }

            ActiveGameplayEffect active = _pool.Count > 0 ? _pool.Pop() : new ActiveGameplayEffect();
            active.Initialize(NextHandle(), def, spec.Source, period, duration);
            active.Spec = spec;
            handle = active.Handle;

            ReadOnlySpan<EffectModifier> mods = def.Modifiers;
            if (mods.Length > 0)
            {
                if (active.ResolvedMagnitudes == null || active.ResolvedMagnitudes.Length < mods.Length)
                    active.ResolvedMagnitudes = new float[mods.Length];
                if (resolved) magnitudes.CopyTo(active.ResolvedMagnitudes);
                else for (int i = 0; i < mods.Length; i++) active.ResolvedMagnitudes[i] = spec.Resolve(mods[i].Magnitude, _attributes);
            }

            RemoveEffectsWithAnyTag(tags.RemoveEffectsWith);
            _active.Add(active);

            if (mods.Length > 0)
            {
                if (active.ModifierAttributes == null || active.ModifierAttributes.Length < mods.Length)
                    active.ModifierAttributes = new GameplayAttribute[mods.Length];
                active.ModifierAttributeCount = mods.Length;
                for (int i = 0; i < mods.Length; i++) _attributes.TryGet(mods[i].Target, out active.ModifierAttributes[i]);
                if (active.ResolvedPeriod <= 0f)
                {
                    active.HasOngoingModifiers = true;
                    for (int i = 0; i < mods.Length && !active.PendingRemove; i++)
                        active.ModifierAttributes[i]?.AddModifier(mods[i].Operation, active.ResolvedMagnitudes[i], active);
                }
            }

            for (int i = 0; i < tags.Granted.Count && !active.PendingRemove; i++)
            {
                active.GrantedTagCount++;
                _ownedTags.AddTag(tags.Granted[i]);
            }
            if (active.PendingRemove) return EffectApplicationResult.Applied;

            GasDebugHooks.Apply(_owner, def, spec.Source, active.StackCount);
            OnEffectAdded?.Invoke(CreateInfo(active));
            if (active.PendingRemove) return EffectApplicationResult.Applied;

            DispatchCues(def, GameplayCueEvent.OnActive, spec.Source, active.StackCount);
            ApplyConditional(spec, EffectTrigger.Applied);
            if (def.ExecutePeriodicOnApplication && active.ResolvedPeriod > 0f && !active.PendingRemove) ApplyPeriodic(active);
            return EffectApplicationResult.Applied;
        }

        public bool Remove(ActiveGameplayEffectHandle handle)
        {
            if (!handle.IsValid) return false;
            _opDepth++;
            try { var e = FindByHandle(handle); return e != null && MarkForRemoval(e); }
            finally { EndOp(); }
        }

        public bool RemoveStacks(ActiveGameplayEffectHandle handle, int count = 1)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (!handle.IsValid) return false;
            _opDepth++;
            try
            {
                ActiveGameplayEffect e = FindByHandle(handle);
                if (e == null) return false;
                if (e.StackCount <= count) return MarkForRemoval(e);
                e.StackCount -= count;
                PushStackCount(e);
                GasDebugHooks.Stack(_owner, e.Definition, e.Source, e.StackCount);
                OnEffectStackChanged?.Invoke(CreateInfo(e));
                return true;
            }
            finally { EndOp(); }
        }

        public bool ModifyRemainingTime(ActiveGameplayEffectHandle handle, float deltaSeconds)
        {
            if (!handle.IsValid) return false;
            _opDepth++;
            try { var e = FindByHandle(handle); return e != null && AdjustRemainingTime(e, deltaSeconds); }
            finally { EndOp(); }
        }

        public int ModifyRemainingTimeWithAnyTag(in GameplayTagSet tags, float deltaSeconds)
        {
            if (tags.IsEmpty) return 0;
            _opDepth++;
            try
            {
                int modified = 0;
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    ActiveGameplayEffect e = _active[i];
                    if (!e.PendingRemove && e.Definition.DurationPolicy == EffectDurationPolicy.Duration &&
                        e.Definition.Tags.Granted.HasAnyMatching(tags) && AdjustRemainingTime(e, deltaSeconds))
                        modified++;
                }
                return modified;
            }
            finally { EndOp(); }
        }

        public int RemoveEffectsWithAnyTag(in GameplayTagSet tags)
        {
            if (tags.IsEmpty) return 0;
            _opDepth++;
            try
            {
                int removed = 0;
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    ActiveGameplayEffect e = _active[i];
                    if (!e.PendingRemove && e.Definition.Tags.Granted.HasAnyMatching(tags) && MarkForRemoval(e)) removed++;
                }
                return removed;
            }
            finally { EndOp(); }
        }

        public bool TryGetLongestRemainingWithAnyTag(in GameplayTagSet tags, out float remaining, out float duration)
        {
            remaining = duration = 0f;
            if (tags.IsEmpty) return false;
            bool found = false;
            for (int i = 0; i < _active.Count; i++)
            {
                ActiveGameplayEffect e = _active[i];
                if (e.PendingRemove || !e.Definition.Tags.Granted.HasAnyMatching(tags)) continue;
                float r = e.Definition.DurationPolicy == EffectDurationPolicy.Duration ? e.RemainingTime : 0f;
                if (!found || r > remaining || (r == remaining && e.ResolvedDuration > duration))
                {
                    found = true; remaining = r; duration = e.ResolvedDuration;
                }
            }
            return found;
        }

        public ActiveGameplayEffectHandle Find(GameplayEffectDefinition definition)
        {
            for (int i = 0; i < _active.Count; i++)
                if (!_active[i].PendingRemove && ReferenceEquals(_active[i].Definition, definition)) return _active[i].Handle;
            return default;
        }

        public ActiveGameplayEffectHandle FindFrom(GameplayEffectDefinition definition, object source)
        {
            if (source is AbilitySystemComponent owner) source = owner.SourceIdentity;
            for (int i = 0; i < _active.Count; i++)
                if (!_active[i].PendingRemove && ReferenceEquals(_active[i].Definition, definition) && ReferenceEquals(_active[i].Source, source))
                    return _active[i].Handle;
            return default;
        }

        public bool TryGetInfo(ActiveGameplayEffectHandle handle, out ActiveGameplayEffectInfo info)
        {
            ActiveGameplayEffect e = FindByHandle(handle);
            if (e == null) { info = default; return false; }
            info = CreateInfo(e);
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime < 0f) throw new ArgumentOutOfRangeException(nameof(deltaTime));
            if (_ticking) throw new InvalidOperationException("GameplayEffectContainer.Tick cannot be called recursively.");
            if (_active.Count == 0) return;

            _ticking = true;
            _opDepth++;
            try
            {
                int count = _active.Count;
                for (int i = 0; i < count && i < _active.Count; i++)
                {
                    ActiveGameplayEffect e = _active[i];
                    if (e.PendingRemove) continue;
                    GameplayEffectDefinition def = e.Definition;
                    if (!def.OngoingRequirements.Matches(_ownedTags)) { MarkForRemoval(e); continue; }

                    float dt = def.DurationPolicy == EffectDurationPolicy.Duration
                        ? Math.Min(deltaTime, Math.Max(0f, e.ResolvedDuration - e.ElapsedTime)) : deltaTime;
                    e.ElapsedTime += dt;
                    if (e.ResolvedPeriod > 0f)
                    {
                        e.PeriodTimer += dt;
                        while (!e.PendingRemove && e.PeriodTimer >= e.ResolvedPeriod)
                        {
                            e.PeriodTimer -= e.ResolvedPeriod;
                            ApplyPeriodic(e);
                        }
                    }
                    if (!e.PendingRemove && def.DurationPolicy == EffectDurationPolicy.Duration && e.ElapsedTime >= e.ResolvedDuration)
                        MarkForRemoval(e);
                }
            }
            finally { try { EndOp(); } finally { _ticking = false; } }
        }

        public void Clear()
        {
            if (_clearing) return;
            _clearing = true;
            _opDepth++;
            try
            {
                int passes = 0;
                while (Count > 0)
                {
                    if (++passes > MaxClearPasses)
                        throw new InvalidOperationException("GameplayEffectContainer.Clear exceeded maximum passes.");
                    for (int i = _active.Count - 1; i >= 0; i--)
                        if (!_active[i].PendingRemove) MarkForRemoval(_active[i]);
                }
            }
            finally { try { EndOp(); } finally { _clearing = false; } }
        }

        private bool AdjustRemainingTime(ActiveGameplayEffect e, float delta)
        {
            if (e.Definition.DurationPolicy != EffectDurationPolicy.Duration) return false;
            float next = e.RemainingTime + delta;
            if (next <= 0f) { MarkForRemoval(e); return true; }
            if (next > e.ResolvedDuration) { e.ResolvedDuration = next; e.ElapsedTime = 0f; }
            else e.ElapsedTime = e.ResolvedDuration - next;
            return true;
        }

        private void EndOp()
        {
            if (--_opDepth != 0 || _pendingRemoves == 0) return;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                ActiveGameplayEffect e = _active[i];
                if (!e.PendingRemove) continue;
                int last = _active.Count - 1;
                _active[i] = _active[last];
                _active.RemoveAt(last);
                if (e.FullyDetached) { e.PrepareForPool(); _pool.Push(e); }
            }
            _pendingRemoves = 0;
        }

        private bool MarkForRemoval(ActiveGameplayEffect e)
        {
            if (e.PendingRemove) return false;
            e.PendingRemove = true;
            _pendingRemoves++;
            if (e.HasOngoingModifiers)
                for (int i = 0; i < e.ModifierAttributeCount; i++) e.ModifierAttributes[i]?.RemoveModifiersFromSource(e);
            for (int i = 0; i < e.GrantedTagCount; i++) _ownedTags.RemoveTag(e.Definition.Tags.Granted[i]);
            GasDebugHooks.Remove(_owner, e.Definition, e.Source, e.StackCount);
            OnEffectRemoved?.Invoke(CreateInfo(e));
            DispatchCues(e.Definition, GameplayCueEvent.Removed, e.Source, e.StackCount);
            ApplyConditional(e.Spec, EffectTrigger.Removed);
            e.FullyDetached = true;
            return true;
        }

        private static ActiveGameplayEffectHandle NextHandle()
        {
            long v; do { v = Interlocked.Increment(ref s_nextHandle); } while (v == 0);
            return new ActiveGameplayEffectHandle(v);
        }

        private ActiveGameplayEffect FindByHandle(ActiveGameplayEffectHandle handle)
        {
            if (!handle.IsValid) return null;
            for (int i = 0; i < _active.Count; i++)
                if (!_active[i].PendingRemove && _active[i].Handle == handle) return _active[i];
            return null;
        }

        private static ActiveGameplayEffectInfo CreateInfo(ActiveGameplayEffect e)
            => new(e.Handle, e.Definition, e.Source, e.StackCount, e.RemainingTime, e.ResolvedDuration, e.ResolvedPeriod);

        private void DispatchCues(GameplayEffectDefinition def, GameplayCueEvent evt, object source, int stacks)
        {
            if (_cueRouter == null) return;
            GameplayTagSet cues = def.CueTags;
            for (int i = 0; i < cues.Count; i++) _cueRouter.Dispatch(new GameplayCueParameters(cues[i], evt, _owner, source, stacks));
        }

        private void ApplyConditional(in GameplayEffectSpec spec, EffectTrigger trigger)
        {
            ReadOnlySpan<ConditionalEffect> children = spec.Definition.ConditionalEffects;
            if (children.Length == 0) return;
            if (_chainDepth >= MaxChainDepth) throw new InvalidOperationException($"Conditional effect chain exceeded depth {MaxChainDepth}.");
            _chainDepth++;
            try
            {
                for (int i = 0; i < children.Length; i++)
                    if (children[i].Trigger == trigger) Apply(spec.ForChild(children[i].Effect));
            }
            finally { _chainDepth--; }
        }

        private ActiveGameplayEffect FindStackable(in GameplayEffectSpec spec)
        {
            EffectStacking mode = spec.Definition.Stacking.Mode;
            if (mode == EffectStacking.None) return null;
            for (int i = 0; i < _active.Count; i++)
            {
                ActiveGameplayEffect e = _active[i];
                if (!e.PendingRemove && ReferenceEquals(e.Definition, spec.Definition) &&
                    (mode == EffectStacking.AggregateByTarget || ReferenceEquals(e.Source, spec.Source)))
                    return e;
            }
            return null;
        }

        private static void PushStackCount(ActiveGameplayEffect e)
        {
            if (!e.HasOngoingModifiers) return;
            for (int i = 0; i < e.ModifierAttributeCount; i++) e.ModifierAttributes[i]?.SetSourceStackCount(e, e.StackCount);
        }

        private static void RefreshTimers(ActiveGameplayEffect e)
        {
            EffectStackingRule rule = e.Definition.Stacking;
            if (rule.DurationRefresh == StackDurationRefreshPolicy.RefreshOnApply) e.ElapsedTime = 0f;
            if (rule.PeriodReset == StackPeriodResetPolicy.ResetOnApply) e.PeriodTimer = 0f;
        }

        private void ApplyPeriodic(ActiveGameplayEffect e)
        {
            if (e.ResolvedMagnitudes != null && e.ModifierAttributeCount > 0)
            {
                ReadOnlySpan<EffectModifier> mods = e.Definition.Modifiers;
                for (int i = 0; i < e.ModifierAttributeCount; i++)
                    if (e.ModifierAttributes[i] != null) ApplyToBase(e.ModifierAttributes[i], mods[i].Operation, e.ResolvedMagnitudes[i], e.StackCount);
            }
            GasDebugHooks.Period(_owner, e.Definition, e.Source, e.StackCount);
            DispatchCues(e.Definition, GameplayCueEvent.Executed, e.Source, e.StackCount);
            ApplyConditional(e.Spec, EffectTrigger.Periodic);
        }

        private void ApplyInstant(in GameplayEffectSpec spec, int stacks)
        {
            ReadOnlySpan<EffectModifier> mods = spec.Definition.Modifiers;
            if (mods.Length == 0) return;
            float[] rented = null;
            Span<float> resolved = mods.Length <= 128 ? stackalloc float[mods.Length] : (rented = ArrayPool<float>.Shared.Rent(mods.Length));
            try
            {
                for (int i = 0; i < mods.Length; i++) resolved[i] = spec.Resolve(mods[i].Magnitude, _attributes);
                ApplyMagnitudes(mods, resolved, stacks);
            }
            finally { if (rented != null) ArrayPool<float>.Shared.Return(rented); }
        }

        private void ApplyMagnitudes(ReadOnlySpan<EffectModifier> mods, ReadOnlySpan<float> values, int stacks)
        {
            for (int i = 0; i < mods.Length; i++)
                if (_attributes.TryGet(mods[i].Target, out GameplayAttribute attr)) ApplyToBase(attr, mods[i].Operation, values[i], stacks);
        }

        private static void ApplyToBase(GameplayAttribute attr, ModifierOperation op, float mag, int stacks)
        {
            switch (op)
            {
                case ModifierOperation.Add: attr.BaseValue += mag * stacks; break;
                case ModifierOperation.Multiply: attr.BaseValue *= 1f + (mag - 1f) * stacks; break;
                case ModifierOperation.Divide: attr.BaseValue /= 1f + (mag - 1f) * stacks; break;
                case ModifierOperation.Override: attr.BaseValue = mag; break;
            }
        }
    }
}
