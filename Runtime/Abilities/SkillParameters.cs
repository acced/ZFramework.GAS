using System;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>Development-only usage checks. Release players trust built configuration.</summary>
    public static class GasAssert
    {
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        public static void That(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }

    public enum SkillParameterKind { Float, Int, Bool }

    /// <summary>A typed value. IDs, rather than display names, are used for all lookups.</summary>
    public readonly struct SkillParameterValue
    {
        public readonly SkillParameterKind Kind;
        public readonly float Number;
        private readonly int _integer;
        public int Integer => Kind == SkillParameterKind.Int ? _integer : throw new InvalidOperationException("参数不是 Int。");
        public bool Boolean => Kind == SkillParameterKind.Bool ? Number != 0 : throw new InvalidOperationException("参数不是 Bool。");
        public SkillParameterValue(float value) { Kind = SkillParameterKind.Float; Number = value; _integer = 0; }
        public SkillParameterValue(int value) { Kind = SkillParameterKind.Int; Number = value; _integer = value; }
        public SkillParameterValue(bool value) { Kind = SkillParameterKind.Bool; Number = value ? 1 : 0; _integer = 0; }
        public static SkillParameterValue FromNumber(SkillParameterKind kind, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("参数必须为有限数。");
            if (kind == SkillParameterKind.Float) return new SkillParameterValue(value);
            if (kind == SkillParameterKind.Int && value == Math.Floor(value) && (double)value >= int.MinValue && (double)value <= int.MaxValue)
                return new SkillParameterValue((int)value);
            if (kind == SkillParameterKind.Bool && (value == 0 || value == 1)) return new SkillParameterValue(value == 1);
            throw new InvalidOperationException(kind + " 参数需要" + (kind == SkillParameterKind.Bool ? " 0 或 1。" : " Int32 范围内的整数。"));
        }
        public override string ToString() => Kind == SkillParameterKind.Bool ? Boolean.ToString() : Kind == SkillParameterKind.Int ? Integer.ToString() : Number.ToString("0.###");
    }

    /// <summary>Immutable parameters, resolved and cached by the caller before casting.</summary>
    public sealed class SkillParameterValues
    {
        private readonly Dictionary<string, SkillParameterValue> _values;
        public static readonly SkillParameterValues Empty = new SkillParameterValues(null);
        public SkillParameterValues(IEnumerable<KeyValuePair<string, SkillParameterValue>> values)
        {
            _values = new Dictionary<string, SkillParameterValue>(StringComparer.Ordinal);
            if (values != null) foreach (var pair in values) _values.Add(pair.Key, pair.Value);
        }
        public int Count => _values.Count;
        public bool TryGet(string id, out SkillParameterValue value) => _values.TryGetValue(id, out value);
        public SkillParameterValue Get(string id) => TryGet(id, out var value) ? value
            : throw new InvalidOperationException($"Missing skill parameter '{id}'.");
        public void CopyTo(List<KeyValuePair<string, SkillParameterValue>> results)
        {
            results.Clear();
            foreach (var pair in _values) results.Add(pair);
        }
    }

    /// <summary>Level and captured source values survive source recycling and later upgrades.</summary>
    public sealed class EffectEvaluationContext
    {
        private readonly Dictionary<AttributeId, float> _source;
        private AttributeSet _liveSource;
        public bool HasSourceSnapshot => _source != null;
        public int Level { get; }
        public SkillParameterValues Parameters { get; }
        public EffectEvaluationContext(int level, SkillParameterValues parameters = null, AttributeSet source = null)
        {
            Level = Math.Max(1, level);
            Parameters = parameters ?? SkillParameterValues.Empty;
            if (source != null) _source = source.CaptureValues();
        }
        public float SourceValue(AttributeId id, AttributeSet fallback)
            => _source != null ? (_source.TryGetValue(id, out var value) ? value : 0) : (fallback ?? _liveSource)?.GetCurrent(id) ?? 0;
        internal static EffectEvaluationContext Live(int level, AttributeSet source)
            => new EffectEvaluationContext(level) { _liveSource = source };
        public float Parameter(string id)
        {
            var value = Parameters.Get(id);
            GasAssert.That(value.Kind != SkillParameterKind.Bool && float.IsFinite(value.Number), "Invalid numeric parameter.");
            return value.Number;
        }
    }

    /// <summary>Program extension point for bounded, typed calculations; never parses scripts.</summary>
    public interface IEffectCalculation
    {
        float Evaluate(in GameplayEffectSpec spec, AttributeSet target);
    }

}
