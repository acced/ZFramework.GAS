using System;
using System.Collections.Generic;
using GameplayTags;
using ZFramework.GAS;

internal static class ReentryRegressionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Task : GameplayAbilityTask
    {
        public int Ticks;
        public Action Tick;
        protected override void OnTick(float dt) { Ticks++; Tick?.Invoke(); }
    }
    public static void Run()
    {
        var owner = new AbilitySystemComponent();
        GameplayAbilitySpec Grant() { var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec); return spec; }
        GameplayAbilitySpec a, b;
        a = Grant(); b = Grant();
        var restarted = new Task(); var restart = new Task();
        restart.Tick = () => { owner.EndAbility(a); owner.TryActivateAbility(a); owner.StartTask(a, restarted); owner.EndAbility(b);
            bool rejected = false; try { owner.Tick(1); } catch (InvalidOperationException) { rejected = true; } Check(rejected, "Nested Tick must explicitly reject the unprocessed delta."); };
        owner.StartTask(a, restarted); owner.StartTask(b, restart); owner.Tick(1);
        Check(restarted.Ticks == 0, "Restarted task runs and nested Tick must wait for next frame."); owner.Tick(1);
        Check(restarted.Ticks == 1, "Restarted task must progress next frame.");
        for (int i = 0; i < 20; i++) owner.Tick(0);
        long bytes = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) owner.Tick(0);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes, "Warm ASC task scheduling must not allocate."); owner.Clear();

        var health = owner.Attributes.Add(new AttributeId(1), 10000);
        var stack = new GameplayEffectDefinition(EffectDurationPolicy.Duration, 10, period: 1,
            stacking: new EffectStackingRule(EffectStacking.AggregateBySource),
            modifiers: new[] { new EffectModifier(health.Id, ModifierOperation.Add, EffectMagnitude.FromParameter("damage")) });
        var values = new Dictionary<string, SkillParameterValue> { ["damage"] = new SkillParameterValue(-1f) };
        var context = new EffectEvaluationContext(1, new SkillParameterValues(values));
        var effect = new GameplayEffectSpec(stack, source: owner, context: context);
        owner.Effects.Apply(effect); var handle = owner.Effects[0].Handle;
        values["damage"] = new SkillParameterValue(-999f);
        Check(owner.Effects.Apply(new GameplayEffectSpec(stack, source: owner.SourceIdentity)) == EffectApplicationResult.Stacked, "ASC and identity sources must share source aggregation.");
        for (int i = 0; i < 20; i++) owner.Effects.Apply(effect);
        bytes = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) owner.Effects.Apply(effect);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes && owner.Effects[0].Handle.Equals(handle), "Reapplying stackable effects must share immutable parameters without allocation.");
        int count = owner.Effects[0].StackCount; owner.Tick(1);
        Check(health.CurrentValue == 10000 - count, "Stacked periodic effects retain the initially resolved parameters.");
        Console.WriteLine("PASS AscReentryAndStackAllocations");
        PublicApiRegressionTests.Run();
    }
}
