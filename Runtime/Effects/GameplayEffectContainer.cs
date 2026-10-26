using GameplayTags;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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

        public int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _active.Count; i++)
                {
                    if (!_active[i].PendingRemove)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>按当前容器顺序取得只读快照。删除或 swap-remove 不会改变已取得的快照。</summary>
        public ActiveGameplayEffectInfo this[int index] => CreateInfo(_active[index]);

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
                return ApplyCore(spec, out handle);
            }
            finally
            {
                EndOperation();
            }
        }

        private EffectApplicationResult ApplyCore(
            in GameplayEffectSpec spec, out ActiveGameplayEffectHandle handle)
        {
            handle = default;
            GameplayEffectDefinition definition = spec.Definition;
            EffectTags tags = definition.Tags;

            if (!definition.ApplicationRequirements.Matches(_ownedTags))
            {
                return EffectApplicationResult.Blocked;
            }

            if (definition.DurationPolicy == EffectDurationPolicy.Instant)
            {
                ApplyInstant(spec, stackCount: 1);
                RemoveEffectsWithAnyTag(tags.RemoveEffectsWith);
                DispatchCues(definition, GameplayCueEvent.Executed, spec.Source, 1);
                ApplyConditional(definition, EffectTrigger.Applied, spec.SourceAttributes, spec.Source);
                return EffectApplicationResult.Completed;
            }

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
                    return EffectApplicationResult.Refreshed;
                }

                existing.StackCount++;
                PushStackCount(existing);
                RefreshTimers(existing);
                return EffectApplicationResult.Stacked;
            }

            ActiveGameplayEffect active = RentEffect(
                definition, spec.Source, spec.SourceAttributes, ResolvePeriod(spec), ResolveDuration(spec));
            handle = active.Handle;

            // Prepare：先解析全部数值。此时 active 尚未发布到 _active，也没有修改属性或标签；
            // 任一 Resolve 失败都只会归还未发布对象，不会留下半初始化效果或部分 Modifier。
            EffectModifier[] modifiers = definition.Modifiers;
            try
            {
                ResolveMagnitudes(spec, active, modifiers);
            }
            catch
            {
                handle = default;
                ReturnUnpublished(active);
                throw;
            }

            // Commit：全部 magnitude 已解析成功后才让效果对查询、Tick 与回调可见。
            _active.Add(active);

            if (active.ResolvedPeriod <= 0f)
            {
                PrepareModifierAttributes(active, modifiers);
                try
                {
                    for (int i = 0; i < active.ModifierAttributeCount; i++)
                    {
                        GameplayAttribute attribute = active.ModifierAttributes[i];
                        ref readonly EffectModifier modifier = ref modifiers[i];
                        if (attribute == null)
                        {
                            continue;
                        }

                        attribute.AddModifier(
                            new AttributeModifier(modifier.Target, modifier.Operation, active.ResolvedMagnitudes[i]), active);
                    }
                }
                catch (Exception error)
                {
                    // AddModifier 在 Recalculate/事件前已写入结构；逐项清理并验证脱钩后才能回池。
                    Exception firstError = error;
                    for (int i = 0; i < active.ModifierAttributeCount; i++)
                    {
                        GameplayAttribute attribute = active.ModifierAttributes[i];
                        if (attribute == null)
                        {
                            continue;
                        }

                        try
                        {
                            attribute.RemoveModifiersFromSource(active);
                        }
                        catch (Exception cleanupError)
                        {
                            CaptureFirstException(ref firstError, cleanupError);
                        }
                    }

                    _active.Remove(active);
                    handle = default;
                    if (HasModifierReferences(active))
                    {
                        Discard(active);
                    }
                    else
                    {
                        ReturnUnpublished(active);
                    }

                    ExceptionDispatchInfo.Capture(firstError).Throw();
                    throw;
                }
            }

            tags.Granted.AddTo(_ownedTags);

            RemoveEffectsWithAnyTag(tags.RemoveEffectsWith);

            // 回调可立即删除 active；operation scope 保证它在本调用退出前不会被复用。
            DispatchCues(definition, GameplayCueEvent.OnActive, spec.Source, active.StackCount);
            ApplyConditional(definition, EffectTrigger.Applied, spec.SourceAttributes, spec.Source);

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

                    effect.ElapsedTime += deltaTime;

                    if (effect.ResolvedPeriod > 0f)
                    {
                        effect.PeriodTimer += deltaTime;
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
            if (_operationDepth <= 0)
            {
                throw new InvalidOperationException("Gameplay effect operation scope is unbalanced.");
            }

            _operationDepth--;
            if (_operationDepth != 0)
            {
                return;
            }

            FlushPendingRemovals();
        }

        private bool MarkForRemoval(ActiveGameplayEffect effect)
        {
            if (effect == null || effect.PendingRemove)
            {
                return false;
            }

            effect.PendingRemove = true;
            DetachEffect(effect);
            return true;
        }

        private void FlushPendingRemovals()
        {
            Exception firstError = null;
            // PendingRemove 本身就是待删集合，不再维护第二份队列。倒序扫一遍即可 O(n) 完成槽位删除。
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                ActiveGameplayEffect effect = _active[i];
                if (!effect.PendingRemove)
                {
                    continue;
                }

                try
                {
                    int last = _active.Count - 1;
                    _active[i] = _active[last];
                    _active.RemoveAt(last);

                    if (effect.FullyDetached)
                    {
                        ReturnToPool(effect);
                    }
                    else
                    {
                        Discard(effect);
                    }
                }
                catch (Exception error)
                {
                    CaptureFirstException(ref firstError, error);
                }
            }

            if (firstError != null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        private ActiveGameplayEffect RentEffect(
            GameplayEffectDefinition definition,
            object source,
            AttributeSet sourceAttributes,
            float resolvedPeriod,
            float resolvedDuration)
        {
            ActiveGameplayEffect effect = _pool.Count > 0 ? _pool.Pop() : new ActiveGameplayEffect();
            effect.Initialize(NextHandle(), definition, source, sourceAttributes, resolvedPeriod, resolvedDuration);
            return effect;
        }

        private void ReturnUnpublished(ActiveGameplayEffect effect) => ReturnToPool(effect);

        private void ReturnToPool(ActiveGameplayEffect effect)
        {
            effect.PrepareForPool();
            _pool.Push(effect);
        }

        private static void Discard(ActiveGameplayEffect effect)
        {
            // 不进入池，也不由容器保存引用；遗留 Modifier 脱钩后即可由 GC 回收。
            effect.Discard();
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
            Exception firstError = null;
            GameplayTagSet granted = effect.Definition.Tags.Granted;

            // 每个属性单独清理。Recalculate/OnCurrentValueChanged 抛错时，当前属性的结构删除已经完成；
            // 记录异常后继续清理其余属性，不能让一个宿主回调留下别处的 Source 引用。
            for (int i = 0; i < effect.ModifierAttributeCount; i++)
            {
                GameplayAttribute attribute = effect.ModifierAttributes[i];
                if (attribute == null)
                {
                    continue;
                }

                try
                {
                    attribute.RemoveModifiersFromSource(effect);
                }
                catch (Exception error)
                {
                    CaptureFirstException(ref firstError, error);
                }
            }

            // 标签同样逐项移除，避免一个 标签移除回调阻断后续标签的结构清理。
            for (int i = 0; i < granted.Count; i++)
            {
                try
                {
                    _ownedTags.RemoveTag(granted[i]);
                }
                catch (Exception error)
                {
                    CaptureFirstException(ref firstError, error);
                }
            }

            try
            {
                DispatchCues(effect.Definition, GameplayCueEvent.Removed, effect.Source, effect.StackCount);
            }
            catch (Exception error)
            {
                CaptureFirstException(ref firstError, error);
            }

            try
            {
                ApplyConditional(effect.Definition, EffectTrigger.Removed, effect.SourceAttributes, effect.Source);
            }
            catch (Exception error)
            {
                CaptureFirstException(ref firstError, error);
            }

            // 只记录脱钩结果；列表槽位删除与 Retire/Discard 由 lifecycle 在最外层退出时完成。
            effect.FullyDetached = !HasModifierReferences(effect);

            if (firstError != null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        private bool HasModifierReferences(ActiveGameplayEffect effect)
        {
            // 检查应用时实际保存 Source 的属性，而不是重新查询可变 AttributeSet。
            for (int i = 0; i < effect.ModifierAttributeCount; i++)
            {
                GameplayAttribute attribute = effect.ModifierAttributes[i];
                if (attribute != null && attribute.HasModifiersFromSource(effect))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CaptureFirstException(ref Exception firstError, Exception error)
        {
            if (firstError == null)
            {
                firstError = error;
            }
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
                effect.Handle, effect.Definition, effect.Source, effect.StackCount, effect.RemainingTime);

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
            GameplayEffectDefinition definition, EffectTrigger trigger, AttributeSet sourceAttributes, object source)
        {
            ConditionalEffect[] children = definition.ConditionalEffects;
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
                        Apply(new GameplayEffectSpec(child.Effect, sourceAttributes, source));
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
            EffectModifier[] modifiers = effect.Definition.Modifiers;
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

        private float ResolvePeriod(in GameplayEffectSpec spec)
        {
            GameplayEffectDefinition definition = spec.Definition;
            if (definition.PeriodAttribute.HasValue)
            {
                float fromAttribute = spec.SourceAttributes?.GetCurrent(definition.PeriodAttribute.Value) ?? 0f;
                return fromAttribute > 0f ? fromAttribute : 0f;
            }

            return definition.Period > 0f ? definition.Period : 0f;
        }

        private static float ResolveDuration(in GameplayEffectSpec spec)
        {
            GameplayEffectDefinition definition = spec.Definition;
            if (definition.DurationAttribute.HasValue)
            {
                float fromAttribute = spec.SourceAttributes?.GetCurrent(definition.DurationAttribute.Value) ?? 0f;
                if (fromAttribute > 0f)
                {
                    return fromAttribute;
                }
            }

            return definition.Duration;
        }

        private void PrepareModifierAttributes(ActiveGameplayEffect effect, EffectModifier[] modifiers)
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
            in GameplayEffectSpec spec, ActiveGameplayEffect effect, EffectModifier[] modifiers)
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
                EffectModifier[] modifiers = effect.Definition.Modifiers;
                for (int i = 0; i < modifiers.Length; i++)
                {
                    ref readonly EffectModifier modifier = ref modifiers[i];
                    if (_attributes.TryGet(modifier.Target, out GameplayAttribute attribute))
                    {
                        ApplyToBase(attribute, modifier.Operation, magnitudes[i], effect.StackCount);
                    }
                }
            }

            DispatchCues(effect.Definition, GameplayCueEvent.Executed, effect.Source, effect.StackCount);
            ApplyConditional(effect.Definition, EffectTrigger.Periodic, effect.SourceAttributes, effect.Source);
        }

        private void ApplyInstant(in GameplayEffectSpec spec, int stackCount)
        {
            EffectModifier[] modifiers = spec.Definition.Modifiers;
            for (int i = 0; i < modifiers.Length; i++)
            {
                ref readonly EffectModifier modifier = ref modifiers[i];
                if (!_attributes.TryGet(modifier.Target, out GameplayAttribute attribute))
                {
                    continue;
                }

                ApplyToBase(attribute, modifier.Operation, spec.Resolve(modifier.Magnitude, _attributes), stackCount);
            }
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
                {
                    float divisor = 1f + (magnitude - 1f) * stackCount;
                    if (Math.Abs(divisor) > 1e-6f)
                    {
                        attribute.BaseValue /= divisor;
                    }

                    break;
                }
                case ModifierOperation.Override:
                    attribute.BaseValue = magnitude;
                    break;
            }
        }
    }
}
