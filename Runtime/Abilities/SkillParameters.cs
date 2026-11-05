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
        private readonly SkillParameterValues _parent;
        private readonly string _overrideId;
        private readonly SkillParameterValue _overrideValue;
        private SkillParameterValues(SkillParameterValues parent, string id, SkillParameterValue value)
        { _parent = parent; _overrideId = id; _overrideValue = value; }
        public static readonly SkillParameterValues Empty = new SkillParameterValues(null);
        public SkillParameterValues(IEnumerable<KeyValuePair<string, SkillParameterValue>> values)
        {
            _values = new Dictionary<string, SkillParameterValue>(StringComparer.Ordinal);
            if (values != null) foreach (var pair in values) _values.Add(pair.Key, pair.Value);
        }
        public SkillParameterValues With(string id, SkillParameterValue value)
        {
            if(Get(id).Kind!=value.Kind)throw new InvalidOperationException("参数类型不匹配: "+id);
            return new SkillParameterValues(this, id, value);
        }
        public int Count => _parent?.Count ?? _values.Count;
        public bool TryGet(string id, out SkillParameterValue value)
        {
            if (_parent != null) { if (id == _overrideId) { value = _overrideValue; return true; } return _parent.TryGet(id, out value); }
            if (id != null) return _values.TryGetValue(id, out value);
            value = default; return false;
        }
        public SkillParameterValue Get(string id) => TryGet(id, out var value) ? value
            : throw new InvalidOperationException($"Missing skill parameter '{id}'.");
        public void CopyTo(List<KeyValuePair<string, SkillParameterValue>> results)
        {
            if (_parent == null) { results.Clear(); foreach (var pair in _values) results.Add(pair); return; }
            _parent.CopyTo(results);
            for (int i = 0; i < results.Count; i++) if (results[i].Key == _overrideId) { results[i] = new(_overrideId, _overrideValue); break; }
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
        private EffectEvaluationContext(EffectEvaluationContext source, SkillParameterValues parameters)
        { Level = source.Level; Parameters = parameters; _source = source._source; _liveSource = source._liveSource; }
        public EffectEvaluationContext WithParameters(SkillParameterValues parameters)
            => new EffectEvaluationContext(this, parameters ?? SkillParameterValues.Empty);
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
        private readonly string _parameter, _outputMultiplier;
        public AttributeFormula(AttributeId source, AttributeId target, float coefficient = 1,
            float targetCoefficient = 1, float constant = 0, string parameter = null,
            float min = 0, float max = float.MaxValue, float scale = 1)
            : this(source,target,coefficient,targetCoefficient,constant,parameter,min,max,scale,null) {}
        public AttributeFormula(AttributeId source, AttributeId target, float coefficient, float targetCoefficient,
            float constant,string parameter,float min,float max,float scale,string outputMultiplier)
        {
            _outputMultiplier=outputMultiplier;
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
            return Math.Max(_min, Math.Min(_max, value)) * _scale * (string.IsNullOrEmpty(_outputMultiplier)?1:spec.Parameter(_outputMultiplier));
        }
    }
}
