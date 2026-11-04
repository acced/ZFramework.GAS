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
        public Action Tick, End;
        protected override void OnTick(float dt) { Ticks++; Tick?.Invoke(); }
        protected override void OnEnd(bool cancelled) => End?.Invoke();
    }
    public static void Run()
    {
        var owner = new AbilitySystemComponent();
        GameplayAbilitySpec Grant() { var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec); return spec; }
        var a = Grant(); var b = Grant(); var c = Grant();
        var survivor = new Task(); var victim = new Task(); var failing = new Task(); var alreadyTicked = new Task();
        owner.StartTask(a, survivor); owner.StartTask(b, victim);
        failing.Tick = () => { owner.EndAbility(b); throw new InvalidOperationException("tick failure"); };
        owner.StartTask(c, failing); owner.StartTask(a, alreadyTicked);
        try { owner.Tick(1); } catch (InvalidOperationException) { }
        Check(survivor.Ticks == 1 && alreadyTicked.Ticks == 1 && victim.Ticks == 0, "ASC mutation/failure must neither repeat nor skip other tasks.");
        Check(!c.IsActive && c.LastExecutionFailure.Result == AbilityActivationResult.ExecutionFailed, "Raw Core task failure must end its ability with a failure record.");
        owner.StartTask(Grant(), new Task { Tick = () => throw new InvalidOperationException("one") });
        owner.StartTask(Grant(), new Task { Tick = () => throw new InvalidOperationException("two") });
        AggregateException errors = null; try { owner.Tick(1); } catch (AggregateException e) { errors = e; }
        Check(errors?.InnerExceptions.Count == 2 && survivor.Ticks == 2, "ASC reports every failure after progressing peers.");
        owner.Clear();

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

        var tags = new GameplayTagSet("Ability.Attack");
        a = owner.GrantAbility(new GameplayAbilityDefinition(abilityTags: tags)); b = owner.GrantAbility(new GameplayAbilityDefinition(abilityTags: tags));
        owner.TryActivateAbility(a); owner.TryActivateAbility(b);
        owner.StartTask(a, new Task { End = () => throw new InvalidOperationException("cleanup a") });
        owner.StartTask(b, new Task { End = () => { owner.RemoveAbility(a); throw new InvalidOperationException("cleanup b"); } });
        var incoming = owner.GrantAbility(new GameplayAbilityDefinition(cancelAbilitiesWithTags: tags));
        Check(owner.TryActivateAbility(incoming) == AbilityActivationResult.Activated, "A cleanup exception must not abort the interrupting skill.");
        Check(!a.IsActive && !b.IsActive && owner.ActiveTaskCount == 0 && a.LastCleanupError.Contains("cleanup a") && b.LastCleanupError.Contains("cleanup b"), "All affected skills must finish cleanup and retain diagnostics.");

        var attack = new AttributeId(2); owner.Attributes.Add(attack, 20); owner.EndAbility(incoming); owner.TryActivateAbility(incoming, captureSource: true);
        var capture = incoming.EvaluationContext; owner.Attributes.Get(attack).BaseValue = 80;
        Check(capture.SourceValue(attack, owner.Attributes) == 20 && ReferenceEquals(capture, capture.CaptureSource(owner.Attributes)), "Later tasks must reuse the activation's original source values.");
        owner.EndAbility(incoming); owner.TryActivateAbility(incoming, captureSource: true);
        Check(incoming.EvaluationContext.SourceValue(attack, null) == 80 && capture.SourceValue(attack, null) == 20, "New casts capture fresh attributes without mutating old contexts.");

        var stack = new GameplayEffectDefinition(EffectDurationPolicy.Duration, 10, period: 1,
            stacking: new EffectStackingRule(EffectStacking.AggregateBySource));
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["DAMAGE"] = 1 };
        var effect = new GameplayEffectSpec(stack, source: owner, setByCallerValues: values);
        owner.Effects.Apply(effect); var handle = owner.Effects[0].Handle;
        Check(owner.Effects.Apply(new GameplayEffectSpec(stack, source: owner.SourceIdentity)) == EffectApplicationResult.Stacked, "ASC and identity sources must share source aggregation.");
        for (int i = 0; i < 20; i++) owner.Effects.Apply(effect);
        bytes = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) owner.Effects.Apply(effect);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes && owner.Effects[0].Handle.Equals(handle), "Reapplying stackable caller effects must not copy discarded snapshots.");
        Console.WriteLine("PASS AscReentryCleanupSourceCaptureAndStackAllocations");
        PublicApiRegressionTests.Run();
    }
}
