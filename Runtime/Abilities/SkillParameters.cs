using System;
using System.Collections.Generic;

namespace ZFramework.GAS
{
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

    /// <summary>Immutable, per-cast parameter snapshot. Construction is a cast-start allocation.</summary>
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
        public bool TryGet(string id, out SkillParameterValue value)
        {
            if (id != null) return _values.TryGetValue(id, out value);
            value = default; return false;
        }
        public SkillParameterValue Get(string id) => TryGet(id, out var value) ? value
            : throw new InvalidOperationException($"Missing skill parameter '{id}'.");
        public void CopyTo(List<KeyValuePair<string, SkillParameterValue>> results)
        {
            results.Clear(); foreach (var pair in _values) results.Add(pair);
        }
    }

    /// <summary>Level and captured source values survive source recycling and later upgrades.</summary>
    public sealed class EffectEvaluationContext
    {
        private readonly Dictionary<AttributeId, float> _source;
        private AttributeSet _liveSource;
        public bool HasSourceSnapshot => _source != null;
        private EffectEvaluationContext _emptySourceCapture;
        private EffectEvaluationContext _lastCapture;
        private AttributeSet _lastSource;
        private long _lastSourceVersion;
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
            if (value.Kind == SkillParameterKind.Bool) throw new InvalidOperationException("Numeric binding requires Float or Int: " + id);
            if (float.IsNaN(value.Number) || float.IsInfinity(value.Number)) throw new InvalidOperationException("Non-finite parameter: " + id);
            return value.Number;
        }
        private EffectEvaluationContext(EffectEvaluationContext source, SkillParameterValues parameters)
        { Level = source.Level; Parameters = parameters; _source = source._source; _liveSource = source._liveSource; }
        public EffectEvaluationContext WithParameters(SkillParameterValues parameters)
            => new EffectEvaluationContext(this, parameters ?? SkillParameterValues.Empty);
        /// <summary>Legacy asynchronous tasks also need source captures when Core supplied only a frozen level.</summary>
        public EffectEvaluationContext CaptureSource(AttributeSet source)
        {
            if (_source != null || source == null) return this;
            if (source.Count == 0) return _emptySourceCapture ??= new EffectEvaluationContext(Level, Parameters, source);
            if (_lastSource != source || _lastSourceVersion != source.Version || _lastCapture == null)
            {
                _lastCapture = new EffectEvaluationContext(Level, Parameters, source);
                _lastSource = source; _lastSourceVersion = source.Version;
            }
            return _lastCapture;
        }
    }

    /// <summary>Program extension point for bounded, typed calculations; never parses scripts.</summary>
    public interface IEffectCalculation
    {
        float Evaluate(in GameplayEffectSpec spec, AttributeSet target);
    }

    /// <summary>Clamp(constant + source * coefficient * parameter - target * targetCoefficient) * scale.</summary>
    public sealed class AttributeFormula : IEffectCalculation
    {
        private readonly AttributeId _source, _target;
        private readonly float _constant, _coefficient, _targetCoefficient, _min, _max, _scale;
        private readonly string _parameter;
        public AttributeFormula(AttributeId source, AttributeId target, float coefficient = 1,
            float targetCoefficient = 1, float constant = 0, string parameter = null,
            float min = 0, float max = float.MaxValue, float scale = 1)
        {
            if (min > max) throw new ArgumentException("Formula minimum exceeds maximum.");
            _source = source; _target = target; _coefficient = coefficient;
            _targetCoefficient = targetCoefficient; _constant = constant; _parameter = parameter;
            _min = min; _max = max; _scale = scale;
        }
        public float Evaluate(in GameplayEffectSpec spec, AttributeSet target)
        {
            float factor = string.IsNullOrEmpty(_parameter) ? 1 : spec.Parameter(_parameter);
            float value = _constant + spec.SourceValue(_source) * _coefficient * factor
                - (target?.GetCurrent(_target) ?? 0) * _targetCoefficient;
            return Math.Max(_min, Math.Min(_max, value)) * _scale;
        }
    }
}
