using System;
using System.Collections.Generic;
using ZFramework.GAS;

internal static class AuditRegressionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Task : GameplayAbilityTask
    {
        public int Ticks;
        public Action Tick;
        protected override void OnTick(float dt) { Ticks++; Tick?.Invoke(); }
    }
    public static void Run()
    {
        var owner = new AbilitySystemComponent(); var id = new AttributeId(1); var health = owner.Attributes.Add(id, 100);
        GameplayEffectDefinition Effect(EffectMagnitude magnitude, ModifierOperation op = ModifierOperation.Add)
            => new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] { new EffectModifier(id, op, magnitude) });
        var secondId = new AttributeId(2); var second = owner.Attributes.Add(secondId, 100);
        var outer = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
            new EffectModifier(id, ModifierOperation.Add, EffectMagnitude.Fixed(-1)),
            new EffectModifier(secondId, ModifierOperation.Add, EffectMagnitude.Fixed(-2)) });
        var nested = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
            new EffectModifier(secondId, ModifierOperation.Add, EffectMagnitude.Fixed(-10)) });
        Action<GameplayAttribute, float, float> reenter = (_, _, _) => owner.Effects.Apply(new GameplayEffectSpec(nested));
        health.OnCurrentValueChanged += reenter;
        owner.Effects.Apply(new GameplayEffectSpec(outer)); health.OnCurrentValueChanged -= reenter;
        Check(second.BaseValue == 88, "A reentrant instant effect must not overwrite the outer prepared magnitudes."); health.BaseValue = 100;

        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["DAMAGE"] = -1 };
        var damage = Effect(EffectMagnitude.FromCaller("damage"));
        var periodic = new GameplayEffectDefinition(EffectDurationPolicy.Duration, 2, period: 1,
            conditionalEffects: new[] { new ConditionalEffect(damage, EffectTrigger.Periodic) });
        owner.Effects.Apply(new GameplayEffectSpec(periodic, setByCallerValues: values)); values["damage"] = -999;
        owner.Tick(1); Check(health.BaseValue == 99, "Snapshot must preserve comparer and original values."); owner.Effects.Clear();
        values["damage"] = 0;
        var hit = new GameplayEffectSpec(damage, setByCallerValues: values); owner.Effects.Apply(hit);
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) owner.Effects.Apply(hit);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes, "Instant caller values must retain the warmed zero-allocation path.");
        var blocked = new GameplayEffectSpec(new GameplayEffectDefinition(EffectDurationPolicy.Duration, 2,
            tags: new EffectTags(applicationRequired: new GameplayTagSet("Buff.Rage"))), setByCallerValues: values);
        owner.Effects.Apply(blocked); bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) owner.Effects.Apply(blocked);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes, "Blocked persistent effects must not allocate a caller snapshot.");

        var ability = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(ability); ability.Level = 3;
        Check(ability.EvaluationContext?.Level == 1, "Level one must be frozen for the entire activation.");
        var host = new GameplayTaskHost(); var removed = new Task(); var failing = new Task(); var unrelated = new Task();
        failing.Tick = () => { host.Cancel(removed); throw new InvalidOperationException("first"); };
        host.Start(ability, removed); host.Start(ability, failing); host.Start(ability, unrelated);
        try { host.Tick(1); } catch (InvalidOperationException) { }
        Check(unrelated.Ticks == 1 && removed.Ticks == 0, "Mutating a host during failure must not advance peers twice."); host.Clear();
        var restarted = new Task(); var restart = new Task();
        restart.Tick = () => { host.Cancel(restarted); host.Start(ability, restarted); host.Cancel(restart); };
        host.Start(ability, restarted); host.Start(ability, restart); host.Tick(1);
        Check(restarted.Ticks == 0, "New task runs must wait for the next frame."); host.Tick(1); Check(restarted.Ticks == 1, "Restarted task must progress next frame."); host.Clear();
        Console.WriteLine("PASS ReentrantInstantSnapshotsAndStableTaskScheduling");
        ReentryRegressionTests.Run();
    }
}
