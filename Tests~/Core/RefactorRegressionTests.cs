using GameplayTags;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ZFramework.GAS;

internal static class RefactorRegressionTests
{
    public static void Run()
    {
        RunCase(nameof(AttributeClampingEventsValidationAndRollback), AttributeClampingEventsValidationAndRollback);
        RunCase(nameof(AttributeSetFastLookupAndBaseHelpers), AttributeSetFastLookupAndBaseHelpers);
        RunCase(nameof(TagSetSpanEmptyHasAllAndMatchesTag), TagSetSpanEmptyHasAllAndMatchesTag);
        RunCase(nameof(CueRouterIndexedTargetScopedReentryAndCrowdPerf), CueRouterIndexedTargetScopedReentryAndCrowdPerf);
        RunCase(nameof(EventChannelTagScopedHierarchyReentryAndPerf), EventChannelTagScopedHierarchyReentryAndPerf);
        RunCase(nameof(EffectOngoingRequiredStackPoliciesAndDynamicValidation), EffectOngoingRequiredStackPoliciesAndDynamicValidation);
        RunCase(nameof(EffectRemoveStacksModifyRemainingLifecycleAndClearGuard), EffectRemoveStacksModifyRemainingLifecycleAndClearGuard);
        RunCase(nameof(AscLifecycleEventsMultiModifierCostSharedCooldownAndApply), AscLifecycleEventsMultiModifierCostSharedCooldownAndApply);
    }

    private static void RunCase(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ExpectThrows<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception(message);
    }

    private static void AttributeClampingEventsValidationAndRollback()
    {
        var maxHp = new GameplayAttribute(new AttributeId(1), 100f);
        var hp = new GameplayAttribute(new AttributeId(2), 100f)
        {
            PreBaseValueChange = v => Math.Clamp(v, 0f, maxHp.CurrentValue)
        };
        var baseChanges = new List<(float oldVal, float newVal)>();
        var currentChanges = new List<(float oldVal, float newVal)>();
        hp.OnBaseValueChanged += (_, a, b) => baseChanges.Add((a, b));
        hp.OnCurrentValueChanged += (_, a, b) => currentChanges.Add((a, b));

        // Clamp on BaseValue setter
        hp.BaseValue = 150f;
        Check(hp.BaseValue == 100f && baseChanges.Count == 0 && currentChanges.Count == 0,
            "Clamped to unchanged value must not fire change events");
        hp.BaseValue = -25f;
        Check(hp.BaseValue == 0f && hp.CurrentValue == 0f, "HP must clamp to [0, MaxHP]");
        Check(baseChanges.Count == 1 && baseChanges[0] == (100f, 0f), "OnBaseValueChanged records clamped transition");
        Check(currentChanges.Count == 1 && currentChanges[0] == (100f, 0f), "OnCurrentValueChanged matches base clamp");

        // Non-finite input must throw WITHOUT corrupting _baseValue
        ExpectThrows<InvalidOperationException>(() => hp.BaseValue = float.NaN, "NaN must throw");
        ExpectThrows<InvalidOperationException>(() => hp.BaseValue = float.PositiveInfinity, "Infinity must throw");
        Check(hp.BaseValue == 0f && hp.CurrentValue == 0f, "Failed non-finite write must leave attribute intact");

        // PreBaseValueChange also applies to Instant effects via ASC
        var asc = new AbilitySystemComponent();
        var maxAttr = asc.Attributes.Add(new AttributeId(10), 100f);
        var hpAttr = asc.Attributes.Add(new AttributeId(11), 60f);
        hpAttr.PreBaseValueChange = v => Math.Clamp(v, 0f, maxAttr.CurrentValue);
        var heal = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(new AttributeId(11), ModifierOperation.Add, EffectMagnitude.Fixed(80f)) });
        asc.ApplyEffectToSelf(heal);
        Check(hpAttr.BaseValue == 100f && hpAttr.CurrentValue == 100f, "Instant heal clamps to MaxHP");
        var overkill = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(new AttributeId(11), ModifierOperation.Add, EffectMagnitude.Fixed(-999f)) });
        asc.ApplyEffectToSelf(overkill);
        Check(hpAttr.BaseValue == 0f && hpAttr.CurrentValue == 0f, "Instant overkill clamps to 0");

        // AddModifier divide-by-zero rollback must not poison _modifiers
        var atk = new GameplayAttribute(new AttributeId(3), 50f);
        var badSource = new object();
        ExpectThrows<InvalidOperationException>(
            () => atk.AddModifier(ModifierOperation.Divide, 0f, badSource),
            "Divide by zero modifier must throw");
        Check(atk.CurrentValue == 50f, "Rollback restores CurrentValue");
        var goodSource = new object();
        atk.AddModifier(ModifierOperation.Add, 10f, goodSource);
        Check(atk.CurrentValue == 60f, "Subsequent modifiers succeed after divide-by-zero rollback");
        atk.RemoveModifiersFromSource(goodSource);
        Check(atk.CurrentValue == 50f, "Removing good source restores clean base value");
    }

    private static void AttributeSetFastLookupAndBaseHelpers()
    {
        var set = new AttributeSet();
        var low = set.Add(new AttributeId(0), 10f);
        var mid = set.Add(new AttributeId(127), 20f);
        var high = set.Add(new AttributeId(128), 30f);
        var negative = set.Add(new AttributeId(-5), 40f);
        var huge = set.Add(new AttributeId(9999), 50f);

        Check(ReferenceEquals(set.Get(new AttributeId(0)), low), "Fast-path lower bound 0");
        Check(ReferenceEquals(set.Get(new AttributeId(127)), mid), "Fast-path upper bound 127");
        Check(ReferenceEquals(set.Get(new AttributeId(128)), high), "Fallback bound 128");
        Check(ReferenceEquals(set.Get(new AttributeId(-5)), negative), "Fallback negative id");
        Check(ReferenceEquals(set.Get(new AttributeId(9999)), huge), "Fallback large id");
        Check(!set.TryGet(new AttributeId(64), out _), "Unregistered fast-path slot returns false");
        Check(!set.TryGet(new AttributeId(500), out _), "Unregistered fallback id returns false");

        low.AddModifier(ModifierOperation.Add, 5f, new object());
        Check(set.GetCurrent(new AttributeId(0)) == 15f && set.GetBase(new AttributeId(0)) == 10f, "GetBase reads un-modified base");
        set.SetBase(new AttributeId(0), 25f);
        Check(set.GetBase(new AttributeId(0)) == 25f && set.GetCurrent(new AttributeId(0)) == 30f, "SetBase updates base and recalculates");

        // 0-alloc fast-path lookup benchmark
        for (int i = 0; i < 1000; i++) _ = set.GetCurrent(new AttributeId(0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0f;
        for (int i = 0; i < 50000; i++)
        {
            sum += set.GetCurrent(new AttributeId(0)) + set.GetCurrent(new AttributeId(127)) + set.GetBase(new AttributeId(128));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(sum > 0f && allocated == 0, $"AttributeSet lookup must allocate 0 bytes, got {allocated}");
    }

    private static void TagSetSpanEmptyHasAllAndMatchesTag()
    {
        var emptySpan = new GameplayTagSet(ReadOnlySpan<GameplayTag>.Empty);
        Check(emptySpan.IsEmpty && emptySpan.Count == 0, "Empty ReadOnlySpan must produce IsEmpty set");

        var set = new GameplayTagSet("Ability.Attack.Melee", "Buff.Rage");
        Check(set.MatchesTag("Ability.Attack"), "Parent matches descendant in set");
        Check(set.MatchesTag("Ability.Attack.Melee"), "Exact tag matches");
        Check(!set.MatchesTag("Ability.AttackSpeed"), "Sibling must not match");

        var container = new GameplayTagCountContainer();
        container.AddTag("Ability.Attack.Melee");
        container.AddTag("Buff.Rage");
        Check(new GameplayTagSet("Ability.Attack", "Buff").HasAll(container), "HasAll succeeds when container satisfies every required tag");
        Check(GameplayTagSet.Empty.HasAll(container), "Empty set HasAll always succeeds");
        Check(!new GameplayTagSet("Ability.Attack", "Debuff").HasAll(container), "HasAll fails when container lacks any required tag");
        Check(!new GameplayTagSet("Buff").HasAll(new GameplayTagCountContainer()), "Empty container fails non-empty HasAll");
    }

    private static void CueRouterIndexedTargetScopedReentryAndCrowdPerf()
    {
        var router = new GameplayCueRouter();
        var unitA = new AbilitySystemComponent();
        var unitB = new AbilitySystemComponent();

        int globalHits = 0, aHits = 0, bHits = 0;
        Action<GameplayCueParameters> globalHandler = _ => globalHits++;
        Action<GameplayCueParameters> aHandler = _ => aHits++;
        Action<GameplayCueParameters> bHandler = _ => bHits++;

        router.Register("Cue.Hit", globalHandler);
        router.Register("Cue.Hit", unitA, aHandler);
        router.Register("Cue.Hit.Fire", unitB, bHandler);
        Check(router.HandlerCount == 3, "HandlerCount sums global and target-scoped registrations");

        // Target A fires Cue.Hit.Fire -> global + unitA only (NOT unitB)
        router.Dispatch(new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, unitA, unitA, 1));
        Check(globalHits == 1 && aHits == 1 && bHits == 0, "Target-scoped cue isolates to matching target");

        // Target B fires Cue.Hit.Fire -> global + unitB only (NOT unitA)
        router.Dispatch(new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, target: unitB, source: unitA, stackCount: 1));
        Check(globalHits == 2 && aHits == 1 && bHits == 1, "Target-scoped descendant matches unitB");

        // Sibling Cue.HitSpeed -> none
        router.Dispatch(new GameplayCueParameters("Cue.HitSpeed", GameplayCueEvent.Executed, unitA, unitA, 1));
        Check(globalHits == 2 && aHits == 1 && bHits == 1, "Sibling tag never matches parent index");

        // Reentrant unregister + register across hierarchy levels:
        // - Unregistering self does not skip remaining handlers
        // - Unregistering a peer stops that peer from running (no call on disposed target)
        // - Registering a new handler waits for the next dispatch
        int survivorHits = 0, unregisteredPeerHits = 0, lateHits = 0;
        Action<GameplayCueParameters> unregistering = null;
        Action<GameplayCueParameters> unregisteredPeer = _ => unregisteredPeerHits++;
        Action<GameplayCueParameters> survivor = _ => survivorHits++;
        Action<GameplayCueParameters> late = _ => lateHits++;
        unregistering = _ =>
        {
            router.Unregister("Cue.Hit", unitA, unregistering);
            router.Unregister("Cue.Hit.Fire", unitA, unregisteredPeer);
            router.Register("Cue.Hit.Fire", unitA, late);
        };
        router.Register("Cue.Hit", unitA, unregistering);
        router.Register("Cue.Hit", unitA, survivor);
        router.Register("Cue.Hit.Fire", unitA, unregisteredPeer);
        router.Dispatch(new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, unitA, unitA, 1));
        Check(survivorHits == 1 && unregisteredPeerHits == 0 && lateHits == 0,
            "Self-unregister preserves survivor, unregistered peer is skipped, late registration waits");
        router.Dispatch(new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, unitA, unitA, 1));
        Check(survivorHits == 2 && unregisteredPeerHits == 0 && lateHits == 1,
            "Subsequent dispatch runs survivor and newly registered late handler");
        router.Unregister("Cue.Hit", unitA, survivor);
        router.Unregister("Cue.Hit.Fire", unitA, late);

        router.Unregister("Cue.Hit", globalHandler);
        router.Unregister("Cue.Hit", unitA, aHandler);
        router.Unregister("Cue.Hit.Fire", unitB, bHandler);
        Check(router.HandlerCount == 0, "All handlers unregistered cleanly");

        // 100-unit crowd benchmark: target-scoped dispatch must be O(depth) and 0 allocated bytes
        var crowd = new AbilitySystemComponent[100];
        int crowdHits = 0;
        Action<GameplayCueParameters> crowdHandler = _ => crowdHits++;
        for (int i = 0; i < crowd.Length; i++)
        {
            crowd[i] = new AbilitySystemComponent();
            router.Register("Cue.Hit", crowd[i], crowdHandler);
        }
        var sampleParams = new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, target: crowd[50], source: crowd[0], stackCount: 1);
        for (int i = 0; i < 1000; i++) router.Dispatch(sampleParams);
        crowdHits = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 50000; i++) router.Dispatch(sampleParams);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(crowdHits == 50000 && allocated == 0, $"Crowd cue dispatch must allocate 0 bytes, got {allocated}");
        Console.WriteLine($"PERF 50000 target-scoped cue dispatches (100 units): {elapsed * 1000.0 / Stopwatch.Frequency:F2} ms; {allocated} allocated bytes");
    }

    private static void EventChannelTagScopedHierarchyReentryAndPerf()
    {
        var channel = new GameplayEventChannel();
        int parentHits = 0, exactHits = 0, siblingHits = 0, globalHits = 0;
        Action<GameplayEvent> parentHandler = null;
        parentHandler = _ =>
        {
            parentHits++;
            if (parentHits == 2) channel.Unsubscribe("Ability.Attack", parentHandler);
        };
        Action<GameplayEvent> exactHandler = _ => exactHits++;
        Action<GameplayEvent> siblingHandler = _ => siblingHits++;
        channel.Received += _ => globalHits++;

        channel.Subscribe("Ability.Attack", parentHandler);
        channel.Subscribe("Ability.Attack.Melee", exactHandler);
        channel.Subscribe("Ability.AttackSpeed", siblingHandler);

        channel.Send(new GameplayEvent("Ability.Attack.Melee", null, default));
        Check(parentHits == 1 && exactHits == 1 && siblingHits == 0 && globalHits == 1,
            "Hierarchical tag-scoped subscribe notifies ancestors and exact tag only");

        // Second send triggers reentrant unsubscribe of parentHandler while exactHandler still fires
        channel.Send(new GameplayEvent("Ability.Attack.Melee", null, default));
        Check(parentHits == 2 && exactHits == 2 && globalHits == 2, "Reentrant unsubscribe preserves sibling handler");

        // Third send sees parentHandler removed
        channel.Send(new GameplayEvent("Ability.Attack.Melee", null, default));
        Check(parentHits == 2 && exactHits == 3 && globalHits == 3, "Unsubscribed handler is compacted out");

        // 0-alloc perf
        var payload = new GameplayEvent("Ability.Attack.Melee", null, default);
        for (int i = 0; i < 500; i++) channel.Send(payload);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50000; i++) channel.Send(payload);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"GameplayEventChannel.Send must allocate 0 bytes, got {allocated}");
    }

    private static void EffectOngoingRequiredStackPoliciesAndDynamicValidation()
    {
        var asc = new AbilitySystemComponent();
        var hp = asc.Attributes.Add(new AttributeId(1), 100f);

        // OngoingRequired: buff stays while Buff.Rage is present, removed on Tick when Buff.Rage is lost
        asc.OwnedTags.AddTag("Buff.Rage");
        var aura = new GameplayEffectDefinition(EffectDurationPolicy.Infinite,
            tags: new EffectTags(
                granted: new GameplayTagSet("Ability.Attack.Melee"),
                ongoingRequired: new GameplayTagSet("Buff.Rage")));
        asc.ApplyEffectToSelf(aura);
        asc.Tick(0.1f);
        Check(asc.Effects.Count == 1 && asc.OwnedTags.HasTag("Ability.Attack.Melee"), "OngoingRequired satisfied keeps effect");
        asc.OwnedTags.RemoveTag("Buff.Rage");
        asc.Tick(0.1f);
        Check(asc.Effects.Count == 0 && !asc.OwnedTags.HasTag("Ability.Attack.Melee"), "Losing OngoingRequired removes effect");

        // Stacking: NeverRefresh duration + KeepProgress period (DoT does NOT starve!)
        var keepProgressDot = new GameplayEffectDefinition(
            EffectDurationPolicy.Duration,
            duration: 2.5f,
            period: 1f,
            executePeriodicOnApplication: false,
            modifiers: new[] { new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.Fixed(-10f)) },
            stacking: new EffectStackingRule(
                EffectStacking.AggregateByTarget,
                limit: 3,
                durationRefresh: StackDurationRefreshPolicy.NeverRefresh,
                periodReset: StackPeriodResetPolicy.KeepProgress));

        asc.Effects.Apply(new GameplayEffectSpec(keepProgressDot), out var hKeep);
        asc.Tick(0.7f);
        Check(hp.CurrentValue == 100f, "Before first 1s period no tick yet");
        // Apply second stack at t = 0.7s -> KeepProgress keeps 0.3s remaining until next tick, NeverRefresh keeps 1.8s duration!
        asc.Effects.Apply(new GameplayEffectSpec(keepProgressDot));
        Check(asc.Effects.TryGetInfo(hKeep, out var infoKeep) && Math.Abs(infoKeep.RemainingTime - 1.8f) < 1e-4f,
            $"NeverRefresh must preserve 1.8s remaining time, got {infoKeep.RemainingTime}");
        asc.Tick(0.35f); // t = 1.05s -> ticks with 2 stacks (-20)!
        Check(Math.Abs(hp.CurrentValue - 80f) < 1e-4f, $"KeepProgress DoT must tick at 1.0s with 2 stacks (-20), hp={hp.CurrentValue}");

        // Compare with ResetOnApply period + RefreshOnApply duration
        hp.BaseValue = 100f;
        asc.Effects.Remove(hKeep);
        var resetPeriodDot = new GameplayEffectDefinition(
            EffectDurationPolicy.Duration,
            duration: 2.5f,
            period: 1f,
            executePeriodicOnApplication: false,
            modifiers: new[] { new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.Fixed(-10f)) },
            stacking: new EffectStackingRule(
                EffectStacking.AggregateByTarget,
                limit: 3,
                durationRefresh: StackDurationRefreshPolicy.RefreshOnApply,
                periodReset: StackPeriodResetPolicy.ResetOnApply));

        asc.Effects.Apply(new GameplayEffectSpec(resetPeriodDot), out var hReset);
        asc.Tick(0.7f);
        asc.Effects.Apply(new GameplayEffectSpec(resetPeriodDot));
        Check(asc.Effects.TryGetInfo(hReset, out var infoReset) && Math.Abs(infoReset.RemainingTime - 2.5f) < 1e-4f,
            "RefreshOnApply must reset remaining duration to 2.5s");
        asc.Tick(0.35f); // t = 1.05s -> ResetOnApply delayed tick to t = 1.7s, so still 100!
        Check(hp.CurrentValue == 100f, "ResetOnApply delays periodic tick until full period elapses after refresh");
        asc.Tick(0.7f); // t = 1.75s -> ticks with 2 stacks (-20)!
        Check(Math.Abs(hp.CurrentValue - 80f) < 1e-4f, "ResetOnApply ticks after full reset period");
        asc.Effects.Remove(hReset);

        // Dynamic duration/period validation: negative duration clamps to 0, period <= 0 throws
        var negDur = new GameplayEffectDefinition(
            EffectDurationPolicy.Duration,
            durationMagnitude: EffectMagnitude.FromParameter("dur"));
        var negCtx = new EffectEvaluationContext(1, new SkillParameterValues(
            new Dictionary<string, SkillParameterValue> { ["dur"] = new SkillParameterValue(-2f) }));
        asc.Effects.Apply(new GameplayEffectSpec(negDur, context: negCtx), out var hNeg);
        Check(asc.Effects.TryGetInfo(hNeg, out var infoNeg) && infoNeg.RemainingTime == 0f, "Negative resolved duration clamps to 0");
        asc.Tick(0f);
        Check(asc.Effects.Count == 0, "Zero-clamped duration expires on tick");

        var badPeriod = new GameplayEffectDefinition(
            EffectDurationPolicy.Duration,
            duration: 2f,
            periodMagnitude: EffectMagnitude.FromParameter("per"));
        var badPerCtx = new EffectEvaluationContext(1, new SkillParameterValues(
            new Dictionary<string, SkillParameterValue> { ["per"] = new SkillParameterValue(-0.5f) }));
        ExpectThrows<InvalidOperationException>(
            () => asc.Effects.Apply(new GameplayEffectSpec(badPeriod, context: badPerCtx)),
            "Non-positive dynamic period must throw");

        // Periodic cached ModifierAttributes 0-alloc perf
        var fastDot = new GameplayEffectDefinition(
            EffectDurationPolicy.Infinite,
            period: 0.01f,
            modifiers: new[] { new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.Fixed(0f)) });
        asc.ApplyEffectToSelf(fastDot);
        for (int i = 0; i < 100; i++) asc.Tick(0.01f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) asc.Tick(0.01f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"Periodic tick with cached ModifierAttributes must allocate 0 bytes, got {allocated}");
    }

    private static void EffectRemoveStacksModifyRemainingLifecycleAndClearGuard()
    {
        var asc = new AbilitySystemComponent();
        var atk = asc.Attributes.Add(new AttributeId(1), 10f);
        int added = 0, removed = 0, stackEvents = 0, lastStacks = 0;
        asc.Effects.OnEffectAdded += info => { added++; lastStacks = info.StackCount; };
        asc.Effects.OnEffectRemoved += _ => removed++;
        asc.Effects.OnEffectStackChanged += info => { stackEvents++; lastStacks = info.StackCount; };

        var buff = new GameplayEffectDefinition(
            EffectDurationPolicy.Duration,
            duration: 10f,
            modifiers: new[] { new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.Fixed(5f)) },
            tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")),
            stacking: new EffectStackingRule(EffectStacking.AggregateByTarget, limit: 5));

        asc.Effects.Apply(new GameplayEffectSpec(buff), out var handle);
        asc.Effects.Apply(new GameplayEffectSpec(buff));
        asc.Effects.Apply(new GameplayEffectSpec(buff));
        Check(added == 1 && stackEvents == 2 && lastStacks == 3 && atk.CurrentValue == 25f,
            "Stacking fires OnEffectAdded once and OnEffectStackChanged per stack");

        // RemoveStacks(1) -> 2 stacks, modifier scales down to +10
        Check(asc.Effects.RemoveStacks(handle, 1), "RemoveStacks(1) succeeds");
        Check(stackEvents == 3 && lastStacks == 2 && atk.CurrentValue == 20f && removed == 0,
            "RemoveStacks updates modifiers and fires OnEffectStackChanged");

        // ModifyRemainingTime: extend +5s (10 -> 15), then CDR -10s (15 -> 5), then tag CDR -6s (<= 0 -> immediate remove!)
        Check(asc.Effects.ModifyRemainingTime(handle, 5f), "Extend remaining time");
        Check(asc.Effects.TryGetInfo(handle, out var info) && Math.Abs(info.RemainingTime - 15f) < 1e-4f, "Remaining extended to 15s");
        Check(asc.Effects.ModifyRemainingTimeWithAnyTag(new GameplayTagSet("Cooldown"), -10f) == 1, "Batch CDR by parent tag");
        Check(asc.Effects.TryGetInfo(handle, out info) && Math.Abs(info.RemainingTime - 5f) < 1e-4f, "Remaining reduced to 5s");
        Check(asc.Effects.ModifyRemainingTimeWithAnyTag(new GameplayTagSet("Cooldown.Attack"), -6f) == 1, "CDR past 0 removes effect");
        Check(asc.Effects.Count == 0 && removed == 1 && atk.CurrentValue == 10f, "Immediate removal restores attribute and fires OnEffectRemoved");

        // RemoveStacks >= remaining removes effect cleanly
        asc.Effects.Apply(new GameplayEffectSpec(buff), out var h2);
        Check(asc.Effects.RemoveStacks(h2, 5) && asc.Effects.Count == 0 && removed == 2,
            "Removing >= current stacks removes the entire effect");

        // Clear() infinite re-application loop guard
        var loopingAsc = new AbilitySystemComponent();
        var persistent = new GameplayEffectDefinition(EffectDurationPolicy.Infinite);
        loopingAsc.Effects.OnEffectRemoved += _ => loopingAsc.ApplyEffectToSelf(persistent);
        loopingAsc.ApplyEffectToSelf(persistent);
        ExpectThrows<InvalidOperationException>(() => loopingAsc.Effects.Clear(),
            "Infinite re-application during Clear() must fail with InvalidOperationException");
    }

    private static void AscLifecycleEventsMultiModifierCostSharedCooldownAndApply()
    {
        var asc = new AbilitySystemComponent();
        var mana = asc.Attributes.Add(new AttributeId(1), 10f);
        var router = new GameplayCueRouter();
        var target = new AbilitySystemComponent(cueRouter: router);
        var targetHp = target.Attributes.Add(new AttributeId(2), 100f);

        // Multi-modifier net cost: -15 and +10 on Mana => net -5, should succeed with Mana = 10!
        var netCost = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[]
        {
            new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.Fixed(-15f)),
            new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.Fixed(10f))
        });
        var sharedCdA = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 4f,
            tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));
        var sharedCdB = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 2f,
            tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));

        var specA = asc.GrantAbility(new GameplayAbilityDefinition(
            abilityTags: new GameplayTagSet("Ability.Attack.Melee"),
            cost: netCost,
            cooldown: sharedCdA));
        var specB = asc.GrantAbility(new GameplayAbilityDefinition(
            abilityTags: new GameplayTagSet("Ability.AttackSpeed"),
            cooldown: sharedCdB));

        int activatedCount = 0, committedCount = 0, naturalEnds = 0, cancelledEnds = 0;
        asc.OnAbilityActivated += _ => activatedCount++;
        asc.OnAbilityCommitted += _ => committedCount++;
        asc.OnAbilityEnded += (_, cancelled) => { if (cancelled) cancelledEnds++; else naturalEnds++; };

        Check(asc.CheckActivation(specA).Succeeded, "Net cost -5 must pass when Mana is 10 despite first modifier -15");
        Check(asc.TryActivateAbility(specA).Result == AbilityActivationResult.Activated, "Activate specA");
        Check(Math.Abs(mana.CurrentValue - 5f) < 1e-4f, "Net mana spent is 5");
        Check(activatedCount == 1 && committedCount == 1, "Activation & commit events fired");

        // Shared cooldown readback on specB (which was NEVER activated!)
        Check(Math.Abs(specB.CooldownRemaining - 4f) < 1e-4f, $"Shared cooldown remaining on specB should be 4s, got {specB.CooldownRemaining}");
        Check(Math.Abs(specB.CooldownDuration - 4f) < 1e-4f, $"Shared cooldown duration on specB should reflect active 4s CD, got {specB.CooldownDuration}");

        // Natural end vs CancelAbility
        asc.EndAbility(specA);
        Check(naturalEnds == 1 && cancelledEnds == 0, "EndAbility marks cancelled=false");

        asc.Tick(4f);
        Check(specB.CooldownRemaining == 0f, "Shared cooldown expires");
        Check(asc.TryActivateAbility(specB).Result == AbilityActivationResult.Activated, "Activate specB");
        Check(asc.CancelAbility(specB), "CancelAbility cancels active specB");
        Check(!specB.IsActive && naturalEnds == 1 && cancelledEnds == 1, "CancelAbility marks cancelled=true");

        // Hot-reload mid-cooldown preserves active cooldown until it expires, then clears CommittedCooldown cleanly
        var noCdDef = new GameplayAbilityDefinition(abilityTags: new GameplayTagSet("Ability.AttackSpeed"));
        asc.UpdateAbilityDefinition(specB, noCdDef);
        Check(specB.IsOnCooldown && Math.Abs(specB.CooldownRemaining - 2f) < 1e-4f,
            "Hot-reloading definition mid-cooldown preserves active committed cooldown");
        asc.Tick(2f);
        Check(!specB.IsOnCooldown && specB.CooldownRemaining == 0f && specB.CooldownDuration == 0f,
            "Expired committed cooldown clears to zero");
        // Re-applying the old cooldown definition must NOT falsely re-lock the updated no-cooldown ability
        asc.ApplyEffectToSelf(sharedCdB);
        Check(!specB.IsOnCooldown && specB.CooldownRemaining == 0f,
            "Cleared CommittedCooldown must not leak when old cooldown effect is re-applied later");
        asc.Tick(2f);

        // PreparedDefaultContext enables preflight & CooldownDuration before first cast
        var paramCd = new GameplayEffectDefinition(EffectDurationPolicy.Duration,
            durationMagnitude: EffectMagnitude.FromParameter("cd"),
            tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));
        var paramSpec = asc.GrantAbility(new GameplayAbilityDefinition(cooldown: paramCd));
        paramSpec.PreparedDefaultContext = new EffectEvaluationContext(1, new SkillParameterValues(
            new Dictionary<string, SkillParameterValue> { ["cd"] = new SkillParameterValue(7.5f) }));
        Check(Math.Abs(paramSpec.CooldownDuration - 7.5f) < 1e-4f,
            $"PreparedDefaultContext must evaluate CooldownDuration before first cast, got {paramSpec.CooldownDuration}");

        // ApplyEffectToTarget helper passes Source = asc
        var dmg = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(new AttributeId(2), ModifierOperation.Add, EffectMagnitude.Fixed(-35f)) });
        object seenSource = null;
        var cueDmg = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(new AttributeId(2), ModifierOperation.Add, EffectMagnitude.Fixed(-15f)) },
            cueTags: new GameplayTagSet("Cue.Hit.Fire"));
        target.CueRouter.Register("Cue.Hit.Fire", p => seenSource = p.Source);
        Check(asc.ApplyEffectToTarget(target, dmg) == EffectApplicationResult.Completed && targetHp.CurrentValue == 65f,
            "ApplyEffectToTarget applies instant damage");
        Check(asc.ApplyEffectToTarget(target, cueDmg) == EffectApplicationResult.Completed && targetHp.CurrentValue == 50f && ReferenceEquals(seenSource, asc.SourceIdentity),
            "ApplyEffectToTarget propagates Source=asc.SourceIdentity");
    }
}
