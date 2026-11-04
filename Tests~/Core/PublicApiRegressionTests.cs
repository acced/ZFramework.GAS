using System;
using GameplayTags;
using ZFramework.GAS;

internal static class PublicApiRegressionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Calculation : IEffectCalculation
    {
        public Exception Error;
        public float Evaluate(in GameplayEffectSpec spec, AttributeSet target) => Error == null ? -1 : throw Error;
    }
    private sealed class Task : GameplayAbilityTask
    {
        public int Ticks;
        public Action Tick, End, Begin;
        protected override void OnBegin() => Begin?.Invoke();
        protected override void OnTick(float dt) { Ticks++; Tick?.Invoke(); }
        protected override void OnEnd(bool cancelled) => End?.Invoke();
    }
    public static void Run()
    {
        var owner = new AbilitySystemComponent(); var manaId = new AttributeId(1); var healthId = new AttributeId(2); var attackId = new AttributeId(3);
        var mana = owner.Attributes.Add(manaId, 1000); var health = owner.Attributes.Add(healthId, 1000); var attack = owner.Attributes.Add(attackId, 10);
        var victim = owner.GrantAbility(new GameplayAbilityDefinition(abilityTags: new GameplayTagSet("Ability.Attack"))); owner.TryActivateAbility(victim);
        foreach (var error in new Exception[] { new NullReferenceException("custom NRE", new Exception("inner cause")), new ArgumentException("custom argument") })
        {
            var effect = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
                new EffectModifier(manaId, ModifierOperation.Add, EffectMagnitude.FromCalculation(new Calculation { Error = error })) });
            var invalid = owner.GrantAbility(new GameplayAbilityDefinition(cost: effect, cancelAbilitiesWithTags: new GameplayTagSet("Ability.Attack")));
            var check = owner.CheckActivation(invalid);
            Check(check.Result == AbilityActivationResult.InvalidConfiguration && ReferenceEquals(check.Error, error) && check.Error.StackTrace.Contains("Calculation.Evaluate") && owner.DiagnosticCount == 0, "Read-only checks must preserve the original stack/inner exception without writing history.");
            Check(owner.TryActivateAbility(invalid) == AbilityActivationResult.InvalidConfiguration && mana.BaseValue == 1000 && victim.IsActive, "Invalid preflight must not commit or interrupt.");
            Check(invalid.LastActivationCheck.Message.Contains(error.GetType().Name), "Diagnostics must retain the exception type.");
        }
        owner.EndAbility(victim);
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
        owner.Clear(); owner.ClearDiagnostics();
        var first = Grant(); var second = Grant();
        owner.StartTask(first, new Task { End = () => throw new ArgumentException("removed grant cleanup") });
        owner.StartTask(second, new Task { End = () => throw new ArgumentException("recycled owner cleanup") });
        owner.RemoveAbility(first); owner.Clear();
        Check(owner.DiagnosticCount == 2 && owner.GrantedAbilityCount == 0 && owner.GetDiagnostic(0).Message.Contains("removed grant cleanup") && owner.GetDiagnostic(1).Message.Contains("recycled owner cleanup"), "Cleanup diagnostics must survive grant removal and owner recycling.");
        owner.ClearDiagnostics(); Check(owner.DiagnosticCount == 0, "Diagnostics are explicitly acknowledged by the host.");
        Console.WriteLine("PASS PublicApiPreflightLiveSourceRealCostAllocationLookupAndDiagnostics");
        FailureBoundaries();
    }

    private sealed class CountFormatting : Exception
    {
        public int Formatted;
        public override string ToString() { Formatted++; return base.ToString(); }
    }
    private static void FailureBoundaries()
    {
        var owner = new AbilitySystemComponent(); var id = new AttributeId(1); var mana = owner.Attributes.Add(id, 100);
        var tag = new GameplayTagSet("Ability.Attack"); var victim = owner.GrantAbility(new GameplayAbilityDefinition(abilityTags: tag));
        var calc = new Calculation(); var error = new InvalidOperationException("state changed");
        var cost = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] { new EffectModifier(id, ModifierOperation.Add, EffectMagnitude.FromCalculation(calc)) });
        var ability = owner.GrantAbility(new GameplayAbilityDefinition(cost: cost, cancelAbilitiesWithTags: tag));
        owner.TryActivateAbility(victim); owner.StartTask(victim, new Task { End = () => calc.Error = error });
        Check(owner.TryActivateAbility(ability) == AbilityActivationResult.InvalidConfiguration && !victim.IsActive && !ability.IsActive && mana.BaseValue == 100 && ability.LastActivationCheck.Error == error, "Recheck after interruptions before charging the changed cost.");
        calc.Error = null;
        Action<GameplayAttribute, float, float> listener = (_, _, _) => throw error;
        mana.OnCurrentValueChanged += listener;
        Check(owner.TryActivateAbility(ability) == AbilityActivationResult.ExecutionFailed && !ability.IsActive && ability.LastActivationCheck.Error == error && mana.BaseValue == 99, "Commit callback exceptions return failure, without refunding already committed cost.");
        mana.OnCurrentValueChanged -= listener;
        owner.ClearDiagnostics(); owner.TryActivateAbility(ability);
        var begin = new ArgumentException("begin");
        var tick = new Exception("outer task"); var cleanup = new Exception("on end");
        var outer = new Task { Tick = () => {
            try { owner.StartTask(ability, new Task { Begin = () => throw begin, End = () => throw cleanup }); }
            catch (ArgumentException e) when (e == begin) { throw tick; }
        } };
        owner.StartTask(ability, outer);
        try { owner.Tick(.1f); } catch (Exception e) when (e == tick) { }
        Check(!ability.IsActive && owner.ActiveTaskCount == 0 && ability.LastExecutionFailure.Error == begin, "OnBegin failure must end the activation and keep its primary cause.");
        Check(owner.DiagnosticCount == 3 && owner.GetDiagnostic(0).Error == begin && owner.GetDiagnostic(1).Error == cleanup && owner.GetDiagnostic(2).Error == tick, "Begin, cleanup and an independent outer failure must all be retained.");
        owner.ClearDiagnostics(); var host = new GameplayTaskHost(); int oldVersion = ability.ActivationVersion;
        owner.TryActivateAbility(ability);
        host.Start(ability, new Task { Tick = () => throw begin }, oldVersion);
        host.Start(ability, new Task { Tick = () => throw tick }, oldVersion);
        try { host.Tick(.2f); } catch (AggregateException e) { Check(e.InnerExceptions.Count == 2, "Both scene failures propagate."); }
        Check(owner.DiagnosticCount == 2 && owner.GetDiagnostic(0).ActivationVersion == oldVersion && owner.GetDiagnostic(1).ActivationVersion == oldVersion
            && ability.IsActive && ability.LastExecutionFailure.Succeeded, "Record every detached failure against its originating cast without affecting the new cast.");
        owner.ClearDiagnostics();
        var formatting = new CountFormatting();
        for (int i = 0; i < 70; i++) owner.RecordCleanupError(ability, ability.ActivationVersion, formatting, i.ToString());
        Check(formatting.Formatted == 0 && owner.DiagnosticCount == 64 && owner.DroppedDiagnosticCount == 6 && owner.GetDiagnostic(0).SourceId == "6" && owner.GetDiagnostic(63).SourceId == "69", "Bounded diagnostics must remain ordered and never eagerly format exceptions.");
        owner.ClearDiagnostics(); Check(owner.DroppedDiagnosticCount == 0 && owner.DiagnosticCount == 0, "Clearing also acknowledges the overflow count.");
        Console.WriteLine("PASS CommitReentryBeginFailureAndBoundedDiagnostics");
    }
}
