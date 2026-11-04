using GameplayTags;
using System;

namespace ZFramework.GAS
{
    /// <summary>Owner-level error history survives removing grants and recycling the owner.</summary>
    public readonly struct AbilityDiagnostic
    {
        public readonly GameplayAbilityDefinition Definition;
        public readonly int ActivationVersion;
        public readonly bool Cleanup;
        public readonly string SourceId, Message;
        public readonly Exception Error;
        internal readonly GameplayAbilitySpec Origin;
        internal AbilityDiagnostic(GameplayAbilitySpec spec, int version, Exception error, string sourceId, bool cleanup)
        { Origin = spec; Definition = spec.Definition; ActivationVersion = version; Error = error; Message = error.Message; SourceId = sourceId; Cleanup = cleanup; }
    }
    public readonly struct AbilityActivationCheck
    {
        public readonly AbilityActivationResult Result;
        public readonly GameplayTag Tag;
        public readonly AttributeId Attribute;
        public readonly float Amount;
        public readonly string Message;
        public readonly string SourceId;
        /// <summary>Original exception, including its stack and inner cause. Read-only checks never append history.</summary>
        public readonly Exception Error;
        public bool Succeeded => Result == AbilityActivationResult.Activated;
        public AbilityActivationCheck(AbilityActivationResult result, string message = null,
            GameplayTag tag = default, AttributeId attribute = default, float amount = 0, string sourceId = null)
            : this(result, message, tag, attribute, amount, sourceId, null) { }
        private AbilityActivationCheck(AbilityActivationResult result, string message, GameplayTag tag, AttributeId attribute, float amount, string sourceId, Exception error)
        { Result = result; Message = message; Tag = tag; Attribute = attribute; Amount = amount; SourceId = sourceId; Error = error; }
        public static AbilityActivationCheck FromError(AbilityActivationResult result, Exception error, string sourceId = null)
            => new AbilityActivationCheck(result, error.GetType().Name + ": " + error.Message, default, default, 0, sourceId, error);
        public static AbilityActivationCheck PreflightFailure(Exception error, string sourceId = null)
        {
            bool config = error is ArgumentException || error is InvalidOperationException;
            if (error is EffectCalculationValidationException calculation) error = calculation.InnerException;
            return FromError(config ? AbilityActivationResult.InvalidConfiguration : AbilityActivationResult.ExecutionFailed, error, sourceId);
        }
    }
}
