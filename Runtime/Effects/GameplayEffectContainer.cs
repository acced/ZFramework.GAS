using GameplayTags;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace ZFramework.GAS
{
    public enum EffectApplicationResult
    {
        /// <summary>Instant 效果已结算完毕。</summary>
        Completed,
        /// <summary>
        /// 新的持续效果已发布。注意：OnActive 回调可同步移除它，因此返回时 Handle 可能已无法由
        /// <see cref="GameplayEffectContainer.TryGetInfo"/> 解析。
        /// </summary>
        Applied,
        /// <summary>叠加到既有效果上，层数 +1。</summary>
        Stacked,
        /// <summary>已达层数上限且策略为 <see cref="StackOverflowPolicy.Deny"/>，仅刷新了既有效果。</summary>
        Refreshed,
        /// <summary>已达层数上限且策略为 <see cref="StackOverflowPolicy.RemoveExisting"/>，既有效果已被移除。</summary>
        Overflowed,
        /// <summary>标签检查未通过，未产生任何作用。</summary>
        Blocked,
    }

    /// <summary>
    /// 某个目标身上的全部生效效果。外界只持有 <see cref="ActiveGameplayEffectHandle"/>；
    /// 内部状态对象由容器延迟回收并复用，不会把可复用引用泄漏到容器之外。
    /// </summary>
    public sealed class GameplayEffectContainer
    {
        private const int MaxChainDepth = 8;

        private static long s_nextHandle;

        private readonly AttributeSet _attributes;
        private readonly GameplayTagCountContainer _ownedTags;
        private readonly GameplayCueRouter _cueRouter;
        private readonly object _owner;
        private readonly List<ActiveGameplayEffect> _active = new();
        private readonly Stack<ActiveGameplayEffect> _pool = new();
        private int _operationDepth;
        private int _chainDepth;
        private int _pendingRemoveCount;
        private bool _ticking;
        private bool _clearing;

        public GameplayEffectContainer(
            AttributeSet attributes,
            GameplayTagCountContainer ownedTags,
            GameplayCueRouter cueRouter = null,
            object owner = null)
        {
            _attributes = attributes ?? throw new ArgumentNullException(nameof(attributes));
            _ownedTags = ownedTags ?? throw new ArgumentNullException(nameof(ownedTags));
            _cueRouter = cueRouter;
            _owner = owner;
        }

        public int Count => _active.Count - _pendingRemoveCount;

        /// <summary>按当前容器顺序取得只读快照。删除或 swap-remove 不会改变已取得的快照。</summary>
        public ActiveGameplayEffectInfo this[int index]
        {
            get
            {
                if (_pendingRemoveCount == 0) return CreateInfo(_active[index]);
                for (int i = 0; i < _active.Count; i++)
                    if (!_active[i].PendingRemove && index-- == 0) return CreateInfo(_active[i]);
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public EffectApplicationResult Apply(in GameplayEffectSpec spec) => Apply(spec, out _);

        /// <summary>
        /// 应用效果并返回其稳定身份。Instant、Blocked 与 Overflowed 返回无效 Handle；
        /// Stacked/Refreshed 返回既有效果的 Handle。Applied 表示效果曾成功发布，但 OnActive 回调
        /// 可能已同步移除它；需要确认返回时仍存活，应调用 <see cref="TryGetInfo"/>。
        /// </summary>
        public EffectApplicationResult Apply(in GameplayEffectSpec spec, out ActiveGameplayEffectHandle handle)
        {
            BeginOperation();
            try
            {
                return ApplyCore(spec, out handle, default, false, 0, 0);
            }
            finally
            {
                EndOperation();
            }
        }

        internal EffectApplicationResult ApplyResolved(in GameplayEffectSpec spec, ReadOnlySpan<float> magnitudes, float duration, float period)
        {
            BeginOperation();
            try { return ApplyCore(spec, out _, magnitudes, true, duration, period); }
            finally { EndOperation(); }
        }

        private EffectApplicationResult ApplyCore(
            GameplayEffectSpec spec, out ActiveGameplayEffectHandle handle, ReadOnlySpan<float> magnitudes, bool resolved, float duration, float period)
        {
            handle = default;
            GameplayEffectDefinition definition = spec.Definition;
            EffectTags tags = definition.Tags;

            if (!resolved && !definition.ApplicationRequirements.Matches(_ownedTags))
            {
                return EffectApplicationResult.Blocked;
            }

            if (definition.DurationPolicy == EffectDurationPolicy.Instant)
            {
                if (definition.ConditionalEffects.Length != 0) spec = spec.Snapshot();
                if (resolved) ApplyMagnitudes(definition.Modifiers, magnitudes, 1);
                else ApplyInstant(spec, stackCount: 1);
                GasDebugHooks.Apply(_owner, definition, spec.Source, 1);
                RemoveEffectsWithAnyTag(tags.RemoveEffectsWith);
                DispatchCues(definition, GameplayCueEvent.Executed, spec.Source, 1);
                ApplyConditional(spec, EffectTrigger.Applied);
                return EffectApplicationResult.Completed;
            }

            if (!resolved) { period = ResolvePeriod(spec); duration = ResolveDuration(spec); }
            ActiveGameplayEffect existing = FindStackable(spec);
            if (existing != null)
            {
                handle = existing.Handle;
                int limit = definition.Stacking.Limit;
                if (limit > 0 && existing.StackCount >= limit)
                {
                    if (definition.Stacking.Overflow == StackOverflowPolicy.RemoveExisting)
                    {
                        MarkForRemoval(existing);
                        handle = default;
                        return EffectApplicationResult.Overflowed;
                    }

                    RefreshTimers(existing);
                    GasDebugHooks.Refresh(_owner, definition, spec.Source, existing.StackCount);
                    return EffectApplicationResult.Refreshed;
                }

                existing.StackCount++;
                PushStackCount(existing);
                RefreshTimers(existing);
                GasDebugHooks.Stack(_owner, definition, spec.Source, existing.StackCount);
                return EffectApplicationResult.Stacked;
            }

            // Reapplications use the retained spec. Copy caller values only when publishing a new effect.
            spec = spec.Snapshot();
            ActiveGameplayEffect active = RentEffect(
                definition, spec.Source, period, duration);
            active.Spec = spec;
            handle = active.Handle;

            ReadOnlySpan<EffectModifier> modifiers = definition.Modifiers;
            if (resolved)
            {
                if (active.ResolvedMagnitudes == null || active.ResolvedMagnitudes.Length < modifiers.Length)
                    active.ResolvedMagnitudes = new float[modifiers.Length];
                magnitudes.CopyTo(active.ResolvedMagnitudes);
            }
            else ResolveMagnitudes(spec, active, modifiers);
            RemoveEffectsWithAnyTag(tags.RemoveEffectsWith);
            _active.Add(active);
            if (active.ResolvedPeriod <= 0f)
            {
                PrepareModifierAttributes(active, modifiers);
                for (int i = 0; i < modifiers.Length && !active.PendingRemove; i++)
                    active.ModifierAttributes[i]?.AddModifier(
                        new AttributeModifier(modifiers[i].Target, modifiers[i].Operation, active.ResolvedMagnitudes[i]), active);
            }

            for (int i = 0; i < tags.Granted.Count && !active.PendingRemove; i++)
            {
                active.GrantedTagCount++;
                _ownedTags.AddTag(tags.Granted[i]);
            }
            if (active.PendingRemove) return EffectApplicationResult.Applied;
            GasDebugHooks.Apply(_owner, definition, spec.Source, active.StackCount);

            // 回调可立即删除 active；operation scope 保证它在本调用退出前不会被复用。
            DispatchCues(definition, GameplayCueEvent.OnActive, spec.Source, active.StackCount);
            ApplyConditional(spec, EffectTrigger.Applied);

            if (definition.ExecutePeriodicOnApplication && active.ResolvedPeriod > 0f && !active.PendingRemove)
            {
                ApplyPeriodic(active);
            }

            return EffectApplicationResult.Applied;
        }

        public bool Remove(ActiveGameplayEffectHandle handle)
        {
            if (!handle.IsValid)
            {
                return false;
            }

            BeginOperation();
            try
            {
                ActiveGameplayEffect effect = FindByHandle(handle);
                return effect != null && MarkForRemoval(effect);
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>移除所有「授予标签」命中给定集合的效果，返回移除数量。</summary>
        public int RemoveEffectsWithAnyTag(in GameplayTagSet tags)
        {
            if (tags.IsEmpty)
            {
                return 0;
            }

            BeginOperation();
            try
            {
                int removed = 0;
                for (int i = _active.Count - 1; i >= 0; i--)
                {
                    ActiveGameplayEffect effect = _active[i];
                    if (!effect.PendingRemove && effect.Definition.Tags.Granted.HasAnyMatching(tags) && MarkForRemoval(effect))
                    {
                        removed++;
                    }
                }

                return removed;
            }
            finally
            {
                EndOperation();
            }
        }

        public ActiveGameplayEffectHandle Find(GameplayEffectDefinition definition)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                ActiveGameplayEffect effect = _active[i];
                if (!effect.PendingRemove && ReferenceEquals(effect.Definition, definition))
                {
                    return effect.Handle;
                }
            }

            return default;
        }

        /// <summary>按定义与来源引用查找；用于多个同定义光环并存时只移除自己的效果。</summary>
        public ActiveGameplayEffectHandle FindFrom(GameplayEffectDefinition definition, object source)
        {
            if (source is AbilitySystemComponent owner) source = owner.SourceIdentity;
            for (int i = 0; i < _active.Count; i++)
            {
                ActiveGameplayEffect effect = _active[i];
                if (!effect.PendingRemove && ReferenceEquals(effect.Definition, definition) &&
                    ReferenceEquals(effect.Source, source))
                {
                    return effect.Handle;
                }
            }

            return default;
        }

        /// <summary>
        /// 查询 Handle 当前是否仍对应存活效果，并返回浅快照。快照中的 Definition 和 Source 是对象引用；
        /// 长期缓存 Info 会相应延长这些对象的存活期。
        /// </summary>
        public bool TryGetInfo(ActiveGameplayEffectHandle handle, out ActiveGameplayEffectInfo info)
        {
            ActiveGameplayEffect effect = FindByHandle(handle);
            if (effect == null)
            {
                info = default;
                return false;
            }

            info = CreateInfo(effect);
            return true;
        }

        public bool TryGetRemainingTime(ActiveGameplayEffectHandle handle, out float remainingTime)
        {
            if (!TryGetInfo(handle, out ActiveGameplayEffectInfo info))
            {
                remainingTime = 0f;
                return false;
            }

            remainingTime = info.RemainingTime;
            return true;
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(deltaTime), deltaTime, "Delta time must not be negative.");
            }

            if (_ticking)
            {
                throw new InvalidOperationException("GameplayEffectContainer.Tick cannot be called recursively.");
            }

            _ticking = true;
            BeginOperation();
            try
            {
                int count = _active.Count;
                for (int i = 0; i < count && i < _active.Count; i++)
                {
                    ActiveGameplayEffect effect = _active[i];
                    if (effect.PendingRemove)
                    {
                        continue;
                    }

                    GameplayEffectDefinition definition = effect.Definition;
                    if (!definition.OngoingRequirements.Matches(_ownedTags))
                    {
                        MarkForRemoval(effect);
                        continue;
                    }

                    float activeTime = definition.DurationPolicy == EffectDurationPolicy.Duration
                        ? Math.Min(deltaTime, Math.Max(0f, effect.ResolvedDuration - effect.ElapsedTime))
                        : deltaTime;
                    effect.ElapsedTime += activeTime;

                    if (effect.ResolvedPeriod > 0f)
                    {
                        effect.PeriodTimer += activeTime;
                        while (!effect.PendingRemove && effect.PeriodTimer >= effect.ResolvedPeriod)
                        {
                            effect.PeriodTimer -= effect.ResolvedPeriod;
                            ApplyPeriodic(effect);
                        }
                    }

                    if (!effect.PendingRemove && definition.DurationPolicy == EffectDurationPolicy.Duration &&
                        effect.ElapsedTime >= effect.ResolvedDuration)
                    {
                        MarkForRemoval(effect);
                    }
                }
            }
            finally
            {
                try
                {
                    EndOperation();
                }
                finally
                {
                    _ticking = false;
                }
            }
        }

        /// <summary>
        /// 清空容器。Removed 回调期间新建的持续效果也属于本次清理范围；反复清理直到没有存活效果。
        /// 重入 Clear 由最外层调用统一完成。
        /// </summary>
        public void Clear()
        {
            if (_clearing)
            {
                return;
            }

            _clearing = true;
            BeginOperation();
            try
            {
                while (Count > 0)
                {
                    for (int i = _active.Count - 1; i >= 0; i--)
                    {
                        ActiveGameplayEffect effect = _active[i];
                        if (!effect.PendingRemove)
                        {
                            MarkForRemoval(effect);
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    EndOperation();
                }
                finally
                {
                    _clearing = false;
                }
            }
        }

        private void BeginOperation() => _operationDepth++;

        private void EndOperation()
        {
            _operationDepth--;
            if (_operationDepth != 0)
            {
                return;
            }

            FlushPendingRemovals();
        }

        private bool MarkForRemoval(ActiveGameplayEffect effect)
        {
            if (effect.PendingRemove)
            {
                return false;
            }

            effect.PendingRemove = true;
            _pendingRemoveCount++;
            DetachEffect(effect);
            return true;
        }

        private void FlushPendingRemovals()
        {
            if (_pendingRemoveCount == 0) return;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var effect = _active[i];
                if (!effect.PendingRemove) continue;
                int last = _active.Count - 1;
                _active[i] = _active[last]; _active.RemoveAt(last);
                if (effect.FullyDetached) ReturnToPool(effect);
            }
            _pendingRemoveCount = 0;
        }

        private ActiveGameplayEffect RentEffect(
            GameplayEffectDefinition definition,
            object source,
            float resolvedPeriod,
            float resolvedDuration)
        {
            ActiveGameplayEffect effect = _pool.Count > 0 ? _pool.Pop() : new ActiveGameplayEffect();
            effect.Initialize(NextHandle(), definition, source, resolvedPeriod, resolvedDuration);
            return effect;
        }

        private void ReturnToPool(ActiveGameplayEffect effect)
        {
            effect.PrepareForPool();
            _pool.Push(effect);
        }

        private static ActiveGameplayEffectHandle NextHandle()
        {
            long value;
            do
            {
                value = Interlocked.Increment(ref s_nextHandle);
            }
            while (value == 0);

            return new ActiveGameplayEffectHandle(value);
        }

        private void DetachEffect(ActiveGameplayEffect effect)
        {
            for (int i = 0; i < effect.ModifierAttributeCount; i++)
                effect.ModifierAttributes[i]?.RemoveModifiersFromSource(effect);
            for (int i = 0; i < effect.GrantedTagCount; i++)
                _ownedTags.RemoveTag(effect.Definition.Tags.Granted[i]);
            GasDebugHooks.Remove(_owner, effect.Definition, effect.Source, effect.StackCount);
            DispatchCues(effect.Definition, GameplayCueEvent.Removed, effect.Source, effect.StackCount);
            ApplyConditional(effect.Spec, EffectTrigger.Removed);
            effect.FullyDetached = true;
        }

        private ActiveGameplayEffect FindByHandle(ActiveGameplayEffectHandle handle)
        {
            if (!handle.IsValid)
            {
                return null;
            }

            for (int i = 0; i < _active.Count; i++)
            {
                ActiveGameplayEffect effect = _active[i];
                if (!effect.PendingRemove && effect.Handle == handle)
                {
                    return effect;
                }
            }

            return null;
        }

        private static ActiveGameplayEffectInfo CreateInfo(ActiveGameplayEffect effect) =>
            new ActiveGameplayEffectInfo(
                effect.Handle, effect.Definition, effect.Source, effect.StackCount, effect.RemainingTime,
                effect.ResolvedDuration, effect.ResolvedPeriod);

        private void DispatchCues(GameplayEffectDefinition definition, GameplayCueEvent cueEvent, object source, int stackCount)
        {
            if (_cueRouter == null)
            {
                return;
            }

            GameplayTagSet cueTags = definition.CueTags;
            for (int i = 0; i < cueTags.Count; i++)
            {
                _cueRouter.Dispatch(new GameplayCueParameters(cueTags[i], cueEvent, _owner, source, stackCount));
            }
        }

        private void ApplyConditional(
            in GameplayEffectSpec spec, EffectTrigger trigger)
        {
            ReadOnlySpan<ConditionalEffect> children = spec.Definition.ConditionalEffects;
            if (children.Length == 0)
            {
                return;
            }

            if (_chainDepth >= MaxChainDepth)
            {
                throw new InvalidOperationException(
                    $"Conditional effect chain exceeded depth {MaxChainDepth}; the effect definitions likely form a cycle.");
            }

            _chainDepth++;
            try
            {
                for (int i = 0; i < children.Length; i++)
                {
                    ref readonly ConditionalEffect child = ref children[i];
                    if (child.Trigger == trigger)
                    {
                        Apply(spec.ForChild(child.Effect));
                    }
                }
            }
            finally
            {
                _chainDepth--;
            }
        }

        private ActiveGameplayEffect FindStackable(in GameplayEffectSpec spec)
        {
            EffectStacking mode = spec.Definition.Stacking.Mode;
            if (mode == EffectStacking.None)
            {
                return null;
            }

            for (int i = 0; i < _active.Count; i++)
            {
                ActiveGameplayEffect effect = _active[i];
                if (effect.PendingRemove || !ReferenceEquals(effect.Definition, spec.Definition))
                {
                    continue;
                }

                if (mode == EffectStacking.AggregateByTarget || ReferenceEquals(effect.Source, spec.Source))
                {
                    return effect;
                }
            }

            return null;
        }

        private void PushStackCount(ActiveGameplayEffect effect)
        {
            ReadOnlySpan<EffectModifier> modifiers = effect.Definition.Modifiers;
            for (int i = 0; i < modifiers.Length; i++)
            {
                if (_attributes.TryGet(modifiers[i].Target, out GameplayAttribute attribute))
                {
                    attribute.SetSourceStackCount(effect, effect.StackCount);
                }
            }
        }

        private static void RefreshTimers(ActiveGameplayEffect effect)
        {
            effect.ElapsedTime = 0f;
            effect.PeriodTimer = 0f;
        }

        private float ResolvePeriod(in GameplayEffectSpec spec) => spec.ResolvePeriod(_attributes);
        private float ResolveDuration(in GameplayEffectSpec spec) => spec.ResolveDuration(_attributes);

        private void PrepareModifierAttributes(ActiveGameplayEffect effect, ReadOnlySpan<EffectModifier> modifiers)
        {
            if (effect.ModifierAttributes == null || effect.ModifierAttributes.Length < modifiers.Length)
            {
                effect.ModifierAttributes = new GameplayAttribute[modifiers.Length];
            }

            effect.ModifierAttributeCount = modifiers.Length;
            for (int i = 0; i < modifiers.Length; i++)
            {
                _attributes.TryGet(modifiers[i].Target, out effect.ModifierAttributes[i]);
            }
        }

        private void ResolveMagnitudes(
            in GameplayEffectSpec spec, ActiveGameplayEffect effect, ReadOnlySpan<EffectModifier> modifiers)
        {
            if (modifiers.Length == 0)
            {
                return;
            }

            if (effect.ResolvedMagnitudes == null || effect.ResolvedMagnitudes.Length < modifiers.Length)
            {
                effect.ResolvedMagnitudes = new float[modifiers.Length];
            }

            for (int i = 0; i < modifiers.Length; i++)
            {
                effect.ResolvedMagnitudes[i] = spec.Resolve(modifiers[i].Magnitude, _attributes);
            }
        }

        private void ApplyPeriodic(ActiveGameplayEffect effect)
        {
            float[] magnitudes = effect.ResolvedMagnitudes;
            if (magnitudes != null)
            {
                ReadOnlySpan<EffectModifier> modifiers = effect.Definition.Modifiers;
                for (int i = 0; i < modifiers.Length; i++)
                {
                    ref readonly EffectModifier modifier = ref modifiers[i];
                    if (_attributes.TryGet(modifier.Target, out GameplayAttribute attribute))
                    {
                        ApplyToBase(attribute, modifier.Operation, magnitudes[i], effect.StackCount);
                    }
                }
            }

            GasDebugHooks.Period(_owner, effect.Definition, effect.Source, effect.StackCount);
            DispatchCues(effect.Definition, GameplayCueEvent.Executed, effect.Source, effect.StackCount);
            ApplyConditional(effect.Spec, EffectTrigger.Periodic);
        }

        private void ApplyInstant(in GameplayEffectSpec spec, int stackCount)
        {
            ReadOnlySpan<EffectModifier> modifiers = spec.Definition.Modifiers;
            float[] rented = null;
            Span<float> resolved = modifiers.Length <= 128 ? stackalloc float[modifiers.Length]
                : (rented = ArrayPool<float>.Shared.Rent(modifiers.Length));
            try
            {
                // Each nested application owns its buffer. Resolve against the pre-effect state
                // before the first write/notification, just like a persistent effect's prepare phase.
                for (int i = 0; i < modifiers.Length; i++) resolved[i] = spec.Resolve(modifiers[i].Magnitude, _attributes);
                ApplyMagnitudes(modifiers, resolved, stackCount);
            }
            finally { if (rented != null) ArrayPool<float>.Shared.Return(rented); }
        }

        private void ApplyMagnitudes(ReadOnlySpan<EffectModifier> modifiers, ReadOnlySpan<float> values, int stacks)
        {
            for (int i = 0; i < modifiers.Length; i++)
                if (_attributes.TryGet(modifiers[i].Target, out var attribute))
                    ApplyToBase(attribute, modifiers[i].Operation, values[i], stacks);
        }

        private static void ApplyToBase(
            GameplayAttribute attribute, ModifierOperation operation, float magnitude, int stackCount)
        {
            switch (operation)
            {
                case ModifierOperation.Add:
                    attribute.BaseValue += magnitude * stackCount;
                    break;
                case ModifierOperation.Multiply:
                    attribute.BaseValue *= 1f + (magnitude - 1f) * stackCount;
                    break;
                case ModifierOperation.Divide:
                    attribute.BaseValue /= 1f + (magnitude - 1f) * stackCount;
                    break;
                case ModifierOperation.Override:
                    attribute.BaseValue = magnitude;
                    break;
            }
        }
    }
}
