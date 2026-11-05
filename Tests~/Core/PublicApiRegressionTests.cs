using System;
using GameplayTags;
using ZFramework.GAS;

internal static class PublicApiRegressionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Task : GameplayAbilityTask
    {
        public int Ticks;
        public Action Tick;
        protected override void OnTick(float dt) { Ticks++; Tick?.Invoke(); }
    }
    private sealed class CountedCalculation : IEffectCalculation
    {
        public int Calls;
        public float Evaluate(in GameplayEffectSpec spec, AttributeSet target) { Calls++; return -1; }
    }
    public static void Run()
    {
        var calculation = new CountedCalculation();
        var calculated = EffectMagnitude.FromCalculation(calculation);
        var testOwner = new AbilitySystemComponent(); testOwner.Attributes.Add(new AttributeId(1), 100);
        var testCost = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] { new EffectModifier(new AttributeId(1), ModifierOperation.Add, calculated) });
        var testCooldown = new GameplayEffectDefinition(EffectDurationPolicy.Duration, 1, modifiers: testCost.Modifiers,
            tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));
        var counted = testOwner.GrantAbility(new GameplayAbilityDefinition(cost: testCost, cooldown: testCooldown));
        testOwner.TryActivateAbility(counted);
        Check(calculation.Calls == 2, "Cost and cooldown magnitudes must each resolve exactly once during activation.");
        testOwner.Clear();
        var owner = new AbilitySystemComponent(); var manaId = new AttributeId(1); var healthId = new AttributeId(2); var attackId = new AttributeId(3);
        var mana = owner.Attributes.Add(manaId, 1000); var health = owner.Attributes.Add(healthId, 1000); var attack = owner.Attributes.Add(attackId, 10);
        var scaled = EffectMagnitude.FromAttribute(attackId, -1, MagnitudeCapture.Source);
        var cost = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] { new EffectModifier(manaId, ModifierOperation.Add, scaled) });
        var cooldown = new GameplayEffectDefinition(EffectDurationPolicy.Duration, .01f, tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));
        var dot = new GameplayEffectDefinition(EffectDurationPolicy.Duration, .01f, period: .01f,
            modifiers: new[] { new EffectModifier(healthId, ModifierOperation.Add, scaled) });
        var spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: cost, cooldown: cooldown), 3);
        void Cast(int i)
        {
            attack.BaseValue = 10 + i % 2;
            Check(owner.TryActivateAbility(spec) == AbilityActivationResult.Activated, "Real-cost test must actually activate.");
            Check(mana.BaseValue == 1000 - attack.BaseValue, "Cost must use this iteration's attack.");
            attack.BaseValue += 5;
            owner.Effects.Apply(new GameplayEffectSpec(dot, context: spec.EvaluationContext));
            owner.EndAbility(spec); owner.Tick(.01f);
            Check(health.BaseValue == 1000 - attack.BaseValue, "A DoT applied with the live cast context must use the changed attack.");
            mana.BaseValue = health.BaseValue = 1000;
        }
        for (int i = 0; i < 100; i++) Cast(i);
        long bytes = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 10000; i++) Cast(i);
        long used = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check(used == 0, "Warmed Core activation with cost, cooldown, DoT and changing attributes must not allocate snapshots.");
        Check(!spec.EvaluationContext.HasSourceSnapshot, "Core default is live source evaluation.");
        var damage = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
            new EffectModifier(healthId, ModifierOperation.Add, EffectMagnitude.FromAttribute(attackId, -1, MagnitudeCapture.Source)) });
        attack.BaseValue = 27; float before = health.BaseValue;
        owner.Effects.Apply(new GameplayEffectSpec(damage, context: spec.EvaluationContext));
        Check(health.BaseValue == before - 27, "Using Core EvaluationContext later must read current source attributes.");
        Console.WriteLine("PERF 10000 Core casts with cost/cooldown/DoT/changing attributes: " + used + " allocated bytes");

        var target = new AbilitySystemComponent(); var identity = owner.SourceIdentity;
        var sustained = new GameplayEffectDefinition(EffectDurationPolicy.Infinite);
        target.Effects.Apply(new GameplayEffectSpec(sustained, source: owner), out var handle);
        Check(target.Effects.FindFrom(sustained, owner).Equals(handle) && target.Effects.FindFrom(sustained, identity).Equals(handle), "Apply and FindFrom must normalize ASC sources identically.");
        owner.Clear(); Check(target.Effects.FindFrom(sustained, owner).Equals(default(ActiveGameplayEffectHandle)), "A recycled ASC must not match its previous incarnation.");
        Check(target.Effects.FindFrom(sustained, identity).Equals(handle), "The original identity still locates its effect.");
        var custom = new object(); target.Effects.Apply(new GameplayEffectSpec(sustained, source: custom), out handle);
        Check(target.Effects.FindFrom(sustained, custom).Equals(handle), "Custom source identities remain supported.");

        GameplayAbilitySpec Grant() { var a = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(a); return a; }
        var low = Grant(); var middle = Grant(); var high = Grant();
        var survivor = new Task(); var removed = new Task(); var remover = new Task { Tick = () => owner.EndAbility(middle) }; var done = new Task();
        owner.StartTask(low, survivor); owner.StartTask(middle, removed); owner.StartTask(high, remover); owner.StartTask(low, done);
        owner.Tick(.1f);
        Check(survivor.Ticks == 1 && remover.Ticks == 1 && done.Ticks == 1 && removed.Ticks == 0, "Nonthrowing removal must not repeat or skip task ticks.");
        owner.Clear();
        Console.WriteLine("PASS LiveSourceRealCostAllocationLookupAndReentry");
    }

}
