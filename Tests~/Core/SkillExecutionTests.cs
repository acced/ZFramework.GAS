using System;
using System.Collections.Generic;
using ZFramework.GAS;

internal static class SkillExecutionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var health = new AttributeId(1); var attack = new AttributeId(2); var armor = new AttributeId(3);
        var owner = new AbilitySystemComponent(); owner.Attributes.Add(attack, 20);
        var target = new AbilitySystemComponent(); target.Attributes.Add(health, 200); target.Attributes.Add(armor, 5);
        var parameters = new Dictionary<string, SkillParameterValue> { ["damage-id"] = new SkillParameterValue(2f) };
        var context = new EffectEvaluationContext(3, new SkillParameterValues(parameters), owner.Attributes);
        parameters["damage-id"] = new SkillParameterValue(999f); owner.Attributes.Get(attack).BaseValue = 999;
        var formula = EffectMagnitude.FromCalculation(new AttributeFormula(attack, armor, parameter: "damage-id", scale: -1));
        var hit = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(health, ModifierOperation.Add, formula) });
        target.Effects.Apply(new GameplayEffectSpec(hit, owner.Attributes, owner, context: context));
        Check(target.Attributes.GetCurrent(health) == 165, "Formula must use immutable cast parameter/source capture and live target armor.");
        var child = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.ByLevel(-1, -2, -3)) });
        var periodic = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 2, period: 1,
            conditionalEffects: new[] { new ConditionalEffect(hit, EffectTrigger.Periodic), new ConditionalEffect(child, EffectTrigger.Removed) });
        target.Effects.Apply(new GameplayEffectSpec(periodic, owner.Attributes, owner, context: context));
        target.Tick(2);
        Check(target.Attributes.GetCurrent(health) == 92, "Periodic and removed child effects inherit the same context.");
        Check(new GameplayEffectSpec(child, context: new EffectEvaluationContext(99)).Resolve(EffectMagnitude.ByLevel(2, 4), null) == 4, "Levels clamp at the last entry.");

        var resource = owner.Attributes.Add(health, 5);
        var cost = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.FromParameter("damage-id", -3)) });
        var ability = owner.GrantAbility(new GameplayAbilityDefinition(cost: cost), 3);
        var check = owner.CheckActivation(ability, context);
        Check(check.Result == AbilityActivationResult.CannotAfford && check.Amount == 1, "Preflight reports the actual missing resource.");
        Check(owner.TryActivateAbility(ability, context) == check.Result && resource.CurrentValue == 5, "Failed preflight and commit must agree without side effects.");
        resource.BaseValue = 10;
        Check(owner.TryActivateAbility(ability, context) == AbilityActivationResult.Activated && resource.CurrentValue == 4, "Commit uses the same resolved cost.");
        var host = new GameplayTaskHost(); var independent = new Timer(); var owned = new Timer();
        host.Start(ability, independent); owner.StartTask(ability, owned); owner.EndAbility(ability);
        Check(!owned.IsActive && independent.IsActive && owner.ActiveTaskCount == 0, "Detached tasks must not remain in ASC or be cancelled with their origin ability.");
        host.Tick(1); Check(host.Count == 0 && !independent.WasCancelled, "Scene task completes under its own host.");
        var oldIdentity = owner.SourceIdentity; owner.Clear(); Check(!ReferenceEquals(oldIdentity, owner.SourceIdentity), "Pool recycling creates a new source identity.");
        Console.WriteLine("PASS SkillExecutionParametersFormulaChildrenAndLifetime");
        Hardening();
        AuditRegressionTests.Run();
    }
    private static void Hardening()
    {
        var owner = new AbilitySystemComponent(); var health = new AttributeId(1); owner.Attributes.Add(health, 100);
        var damage = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.FromCaller("damage")) });
        var periodic = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 3, period: 1,
            conditionalEffects: new[] { new ConditionalEffect(damage, EffectTrigger.Periodic), new ConditionalEffect(damage, EffectTrigger.Removed) });
        var values = new Dictionary<string, float> { ["damage"] = -3 };
        owner.Effects.Apply(new GameplayEffectSpec(periodic, setByCallerValues: values), out var handle); values["damage"] = -999;
        owner.Tick(1); owner.Effects.Remove(handle);
        Check(owner.Attributes.GetCurrent(health) == 94, "Caller values must be frozen for periodic/removed children.");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            foreach (bool duration in new[] { true, false })
            {
                var effect = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 1,
                    durationMagnitude: duration ? EffectMagnitude.FromCaller("time") : (EffectMagnitude?)null,
                    periodMagnitude: duration ? (EffectMagnitude?)null : EffectMagnitude.FromCaller("time"));
                bool rejected = false;
                try { owner.Effects.Apply(new GameplayEffectSpec(effect, setByCallerValues: new Dictionary<string, float> { ["time"] = invalid })); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && owner.Effects.Count == 0, "Non-finite timers must fail before publication.");
            }
        }
        var parameterCost = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.FromParameter("missing")) });
        var invalidAbility = owner.GrantAbility(new GameplayAbilityDefinition(cost: parameterCost));
        Check(owner.TryActivateAbility(invalidAbility) == AbilityActivationResult.InvalidConfiguration, "Missing context must be a diagnostic result.");
        var spec = owner.GrantAbility(new GameplayAbilityDefinition(), 3);
        owner.TryActivateAbility(spec); owner.EndAbility(spec);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { owner.TryActivateAbility(spec); owner.EndAbility(spec); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Warm level 3 activation must not allocate a new context.");
        var host = new GameplayTaskHost(); var good = new Timer(); host.Start(spec, good); host.Start(spec, new ThrowingTimer());
        try { host.Tick(1); } catch (InvalidOperationException) { }
        Check(!good.IsActive && host.Count == 0, "A failed scene task must not skip peers in the same tick.");
        bool badInt = false, badBool = false;
        try { SkillParameterValue.FromNumber(SkillParameterKind.Int, 1.5f); } catch (InvalidOperationException) { badInt = true; }
        try { SkillParameterValue.FromNumber(SkillParameterKind.Bool, 2); } catch (InvalidOperationException) { badBool = true; }
        Check(badInt && badBool, "Typed values must not silently truncate.");
        Console.WriteLine("PASS SkillBoundarySnapshotsTimersHostAndAllocation");
    }
    private sealed class ThrowingTimer : GameplayAbilityTask { protected override void OnTick(float deltaTime) => throw new InvalidOperationException("test"); }
    private sealed class Timer : GameplayAbilityTask
    {
        protected override void OnTick(float deltaTime) { Complete(); }
    }
}
