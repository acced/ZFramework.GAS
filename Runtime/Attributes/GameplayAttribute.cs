using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZFramework.GAS
{
    /// <summary>
    /// 属性标识。库本身不定义任何具体属性，由使用方声明常量：
    /// <c>public static readonly AttributeId Health = new(1);</c>
    /// </summary>
    public readonly struct AttributeId : IEquatable<AttributeId>
    {
        public readonly int Value;

        public AttributeId(int value) => Value = value;

        public bool Equals(AttributeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AttributeId other && Value == other.Value;
        public override int GetHashCode() => Value;
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(AttributeId left, AttributeId right) => left.Value == right.Value;
        public static bool operator !=(AttributeId left, AttributeId right) => left.Value != right.Value;
    }

    public enum ModifierOperation
    {
        Add,
        Multiply,
        Divide,
        Override,
    }

    /// <summary>
    /// 属性修改器。<see cref="Magnitude"/> 必须是**已解析的最终数值**——
    /// 公式求值、MMC 捕获等一律由上层在应用时完成，聚合过程只做纯算术，
    /// 因此不持有计算上下文，也不会在热路径上分配。
    /// </summary>
    public readonly struct AttributeModifier
    {
        public readonly AttributeId Target;
        public readonly ModifierOperation Operation;
        public readonly float Magnitude;

        public AttributeModifier(AttributeId target, ModifierOperation operation, float magnitude)
        {
            Target = target;
            Operation = operation;
            Magnitude = magnitude;
        }
    }

    /// <summary>
    /// 单个属性：权威值 <see cref="BaseValue"/> 与派生值 <see cref="CurrentValue"/>。
    ///
    /// 聚合公式：<c>Current = (Base + ΣAdd) * ΠMultiply / ΠDivide</c>，存在 Override 时直接取其值。
    /// 堆叠语义：Add 按层数线性放大；Multiply/Divide 按 <c>1 + (magnitude - 1) * stacks</c> 累积，
    /// 因此单层时 Divide 即为精确除以 magnitude。
    /// 多个 Override 时后加入者生效（列表保持插入序，故给定相同的应用顺序结果可复现）。
    /// </summary>
    public sealed class GameplayAttribute
    {
        private struct ActiveModifier
        {
            public AttributeModifier Modifier;
            public object Source;
            public int StackCount;
        }

        private readonly List<ActiveModifier> _modifiers = new();
        private float _baseValue;
        private float _currentValue;

        public AttributeId Id { get; }

        /// <summary>CurrentValue 实际变化时触发，参数为 (属性, 旧值, 新值)。</summary>
        public event Action<GameplayAttribute, float, float> OnCurrentValueChanged;

        public GameplayAttribute(AttributeId id, float baseValue = 0f)
        {
            Id = id;
            _baseValue = baseValue;
            _currentValue = baseValue;
        }

        /// <summary>权威值。瞬时效果（伤害、治疗）改这里，持续效果走修改器。</summary>
        public float BaseValue
        {
            get => _baseValue;
            set
            {
                if (_baseValue == value)
                {
                    return;
                }

                _baseValue = value;
                Recalculate();
            }
        }

        /// <summary>派生值，只读。外部无法直接写入，避免与修改器聚合结果产生第二个权威。</summary>
        public float CurrentValue => _currentValue;

        public void AddModifier(in AttributeModifier modifier, object source, int stackCount = 1)
        {
            if (modifier.Target != Id)
            {
                throw new ArgumentException(
                    $"Modifier targets attribute {modifier.Target} but was added to {Id}.", nameof(modifier));
            }

            // 来源是移除修改器的唯一凭据（RemoveModifiersFromSource 按引用比对）。
            // 允许 null 就等于允许加入一个永远摘不掉的修改器。
            if (source == null)
            {
                throw new ArgumentNullException(
                    nameof(source), "Modifier source is required; a modifier without a source could never be removed.");
            }

            _modifiers.Add(new ActiveModifier
            {
                Modifier = modifier,
                Source = source,
                StackCount = stackCount,
            });
            Recalculate();
        }

        /// <summary>移除某来源施加的全部修改器；来源按引用比对。</summary>
        public int RemoveModifiersFromSource(object source)
        {
            if (source == null)
            {
                return 0;
            }

            int removed = 0;
            for (int i = _modifiers.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_modifiers[i].Source, source))
                {
                    continue;
                }

                _modifiers.RemoveAt(i);
                removed++;
            }

            if (removed > 0)
            {
                Recalculate();
            }

            return removed;
        }

        /// <summary>仅供效果容器验证池对象是否已与属性完全脱钩。</summary>
        internal bool HasModifiersFromSource(object source)
        {
            if (source == null)
            {
                return false;
            }

            for (int i = 0; i < _modifiers.Count; i++)
            {
                if (ReferenceEquals(_modifiers[i].Source, source))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>效果堆叠层数变化时由持有方推送，避免聚合过程反向查询来源。</summary>
        public bool SetSourceStackCount(object source, int stackCount)
        {
            if (source == null || stackCount < 0)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < _modifiers.Count; i++)
            {
                ActiveModifier active = _modifiers[i];
                if (!ReferenceEquals(active.Source, source) || active.StackCount == stackCount)
                {
                    continue;
                }

                active.StackCount = stackCount;
                _modifiers[i] = active;
                changed = true;
            }

            if (changed)
            {
                Recalculate();
            }

            return changed;
        }

        private void Recalculate()
        {
            float additive = 0f;
            float multiplier = 1f;
            float divisor = 1f;
            bool hasOverride = false;
            float overrideValue = 0f;

            for (int i = 0; i < _modifiers.Count; i++)
            {
                ActiveModifier active = _modifiers[i];
                float magnitude = active.Modifier.Magnitude;
                int stacks = active.StackCount;

                switch (active.Modifier.Operation)
                {
                    case ModifierOperation.Add:
                        additive += magnitude * stacks;
                        break;
                    case ModifierOperation.Multiply:
                        multiplier *= 1f + (magnitude - 1f) * stacks;
                        break;
                    case ModifierOperation.Divide:
                        divisor *= 1f + (magnitude - 1f) * stacks;
                        break;
                    case ModifierOperation.Override:
                        hasOverride = true;
                        overrideValue = magnitude;
                        break;
                }
            }

            float value;
            if (hasOverride)
            {
                value = overrideValue;
            }
            else
            {
                // 除数为 0 不做特殊处理：结果是 ±无穷或 NaN，会被下面的检查拦下并说清是哪个属性。
                // 早先这里有个 epsilon 保护「跳过除法」，那等于把坏配置变成一个静默的错误数值。
                value = (_baseValue + additive) * multiplier / divisor;
            }

            // NaN / 无穷只可能来自坏数据。放行会永久污染该属性，
            // 且因为 NaN != NaN，变更事件会在此后每次重算都触发一次。
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException(
                    $"Attribute {Id} computed a non-finite value ({value}); check the modifier magnitudes and base value.");
            }

            // 精确相等比较是有意的：只有数值真正改变才通知，用 epsilon 会吞掉合法的微小变化。
            if (value == _currentValue)
            {
                return;
            }

            float previous = _currentValue;
            _currentValue = value;
            OnCurrentValueChanged?.Invoke(this, previous, value);
        }
    }
}
