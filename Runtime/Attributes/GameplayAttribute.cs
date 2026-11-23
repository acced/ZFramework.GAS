using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZFramework.GAS
{
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

    public enum ModifierOperation { Add, Multiply, Divide, Override }

    /// <summary>权威值 BaseValue 与聚合派生值 CurrentValue = (Base + ΣAdd) * ΠMul / ΠDiv（Override 直接覆盖）。</summary>
    public sealed class GameplayAttribute
    {
        private struct ActiveModifier
        {
            public ModifierOperation Operation;
            public float Magnitude;
            public object Source;
            public int StackCount;
        }

        private List<ActiveModifier> _modifiers;
        private float _baseValue;
        private float _currentValue;

        public AttributeId Id { get; }
        public Func<float, float> PreBaseValueChange { get; set; }
        public event Action<GameplayAttribute, float, float> OnBaseValueChanged;
        public event Action<GameplayAttribute, float, float> OnCurrentValueChanged;

        public GameplayAttribute(AttributeId id, float baseValue = 0f)
        {
            if (!float.IsFinite(baseValue))
                throw new ArgumentOutOfRangeException(nameof(baseValue), baseValue, $"Attribute {id} initial base value must be finite.");
            Id = id;
            _baseValue = _currentValue = baseValue;
        }

        public float BaseValue
        {
            get => _baseValue;
            set
            {
                if (!float.IsFinite(value))
                    throw new InvalidOperationException($"Attribute {Id} received a non-finite base value ({value}).");
                if (PreBaseValueChange != null)
                {
                    value = PreBaseValueChange(value);
                    if (!float.IsFinite(value))
                        throw new InvalidOperationException($"Attribute {Id} PreBaseValueChange returned a non-finite value ({value}).");
                }
                if (_baseValue == value) return;

                float prevBase = _baseValue;
                if (_modifiers == null || _modifiers.Count == 0)
                {
                    _baseValue = value;
                    float prevCurrent = _currentValue;
                    _currentValue = value;
                    OnBaseValueChanged?.Invoke(this, prevBase, value);
                    if (prevCurrent != value) OnCurrentValueChanged?.Invoke(this, prevCurrent, value);
                    return;
                }

                float nextCurrent = ComputeCurrentValue(value);
                _baseValue = value;
                OnBaseValueChanged?.Invoke(this, prevBase, value);
                CommitCurrentValue(nextCurrent);
            }
        }

        public float CurrentValue => _currentValue;

        internal void AddModifier(ModifierOperation operation, float magnitude, object source)
        {
            if (!float.IsFinite(magnitude))
                throw new InvalidOperationException($"Attribute {Id} received a non-finite modifier magnitude ({magnitude}).");
            (_modifiers ??= new List<ActiveModifier>(2)).Add(new ActiveModifier
            {
                Operation = operation, Magnitude = magnitude, Source = source, StackCount = 1
            });
            float next;
            try { next = ComputeCurrentValue(_baseValue); }
            catch { _modifiers.RemoveAt(_modifiers.Count - 1); throw; }
            CommitCurrentValue(next);
        }

        internal void RemoveModifiersFromSource(object source)
        {
            if (_modifiers == null || _modifiers.Count == 0) return;
            bool removed = false;
            for (int i = _modifiers.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_modifiers[i].Source, source)) { _modifiers.RemoveAt(i); removed = true; }
            if (removed) CommitCurrentValue(ComputeCurrentValue(_baseValue));
        }

        internal void SetSourceStackCount(object source, int stackCount)
        {
            if (_modifiers == null || _modifiers.Count == 0) return;
            bool changed = false;
            for (int i = 0; i < _modifiers.Count; i++)
            {
                ActiveModifier m = _modifiers[i];
                if (!ReferenceEquals(m.Source, source) || m.StackCount == stackCount) continue;
                m.StackCount = stackCount;
                _modifiers[i] = m;
                changed = true;
            }
            if (changed) CommitCurrentValue(ComputeCurrentValue(_baseValue));
        }

        private float ComputeCurrentValue(float baseValue)
        {
            if (_modifiers == null || _modifiers.Count == 0) return baseValue;
            float add = 0f, mul = 1f, div = 1f, over = 0f;
            bool hasOver = false;
            for (int i = 0; i < _modifiers.Count; i++)
            {
                ActiveModifier m = _modifiers[i];
                switch (m.Operation)
                {
                    case ModifierOperation.Add: add += m.Magnitude * m.StackCount; break;
                    case ModifierOperation.Multiply: mul *= 1f + (m.Magnitude - 1f) * m.StackCount; break;
                    case ModifierOperation.Divide: div *= 1f + (m.Magnitude - 1f) * m.StackCount; break;
                    case ModifierOperation.Override: hasOver = true; over = m.Magnitude; break;
                }
            }
            float value = hasOver ? over : (baseValue + add) * mul / div;
            if (!float.IsFinite(value))
                throw new InvalidOperationException($"Attribute {Id} computed a non-finite value ({value}).");
            return value;
        }

        private void CommitCurrentValue(float value)
        {
            if (value == _currentValue) return;
            float prev = _currentValue;
            _currentValue = value;
            OnCurrentValueChanged?.Invoke(this, prev, value);
        }
    }
}
