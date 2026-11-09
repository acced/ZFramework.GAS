using System;
using GameplayTags;
using ZFramework.GAS;

internal static class ArchitectureRegressionTests
{
    private static readonly AttributeId Resource = new(1);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        ManualCommit();
        CommitRechecksPaymentGates();
        CommitReentry();
        CommitCallbacksCanEndCast();
        PeriodicExpiration();
        ImmutableEffects();
        RemovalDuringPublication();
        ReplaceWithoutSelfRemoval();
        CueClearDuringDispatch();
        SmallDivisors();
        ManualCommitAllocations();
    }

    private static GameplayEffectDefinition Cost(float amount = -10) => new(
        EffectDurationPolicy.Instant,
        modifiers: new[] { new EffectModifier(Resource, ModifierOperation.Add, EffectMagnitude.Fixed(amount)) });

    private static GameplayEffectDefinition Cooldown() => new(EffectDurationPolicy.Duration, 5,
        tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));

    private static void ManualCommit()
    {
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 100);
        var spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: Cost(), cooldown: Cooldown(),
            activationOwnedTags: new GameplayTagSet("Ability.Attack.Melee")));
        Check(owner.TryActivateAbility(spec, autoCommit: false) == AbilityActivationResult.Activated, "Aiming activates.");
        Check(spec.IsActive && !spec.IsCommitted && resource.BaseValue == 100 && !spec.IsOnCooldown,
            "Activation must not charge cost or start cooldown before commit.");
        owner.EndAbility(spec);
        Check(owner.OwnedTags.IsEmpty && resource.BaseValue == 100, "Cancelling aiming releases tags without payment.");

        owner.TryActivateAbility(spec, autoCommit: false);
        int version = spec.ActivationVersion;
        resource.BaseValue = 5;
        Check(owner.CheckCommit(spec).Result == AbilityActivationResult.CannotAfford &&
            owner.CommitAbility(spec, version) == AbilityActivationResult.CannotAfford,
            "Commit must recheck resources changed since activation.");
        Check(resource.BaseValue == 5 && !spec.IsCommitted && !spec.IsOnCooldown, "Rejected commit has no side effects.");
        resource.BaseValue = 100;
        Check(owner.CommitAbility(spec, version) == AbilityActivationResult.Activated && spec.IsCommitted,
            "The active cast can commit once resources permit.");
        owner.Tick(2);
        Check(owner.CommitAbility(spec, version) == AbilityActivationResult.Activated && resource.BaseValue == 90 &&
            spec.CooldownRemaining == 3, "Repeated commit must neither charge twice nor refresh cooldown.");
        owner.EndAbility(spec);
        owner.Tick(3);
        owner.TryActivateAbility(spec, autoCommit: false);
        Check(owner.CommitAbility(spec, version) == AbilityActivationResult.Inactive && !spec.IsCommitted,
            "A callback from an old activation must not commit the new activation.");
        owner.EndAbility(spec);
        Check(owner.CommitAbility(spec) == AbilityActivationResult.Inactive, "Ended abilities cannot commit.");
        Console.WriteLine("PASS ManualCommitCancellationRecheckIdempotenceAndVersion");
    }

    private static void CommitRechecksPaymentGates()
    {
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 100);
        var cooldown = Cooldown();
        var spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: Cost(), cooldown: cooldown));
        owner.TryActivateAbility(spec, autoCommit: false);
        owner.Effects.Apply(new GameplayEffectSpec(cooldown));
        Check(owner.CommitAbility(spec) == AbilityActivationResult.OnCooldown && resource.BaseValue == 100,
            "A shared cooldown started during aiming blocks commit without charging.");
        owner.Clear();

        var gatedCost = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: Cost().Modifiers,
            tags: new EffectTags(applicationBlocked: new GameplayTagSet("Debuff.Poison")));
        spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: gatedCost));
        owner.TryActivateAbility(spec, autoCommit: false);
        owner.OwnedTags.AddTag("Debuff.Poison");
        Check(owner.CommitAbility(spec) == AbilityActivationResult.Blocked && resource.BaseValue == 100,
            "A blocked cost effect cannot silently grant a free cast.");
        owner.EndAbility(spec);
        Check(owner.TryActivateAbility(spec) == AbilityActivationResult.Blocked && !spec.IsActive,
            "Automatic commit uses the same payment gates.");
        Console.WriteLine("PASS CommitRechecksSharedCooldownAndEffectRequirements");
    }

    private static void CommitReentry()
    {
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 100);
        var spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: Cost()));
        int callbacks = 0;
        resource.OnCurrentValueChanged += (_, _, _) =>
        {
            callbacks++;
            Check(owner.CommitAbility(spec) == AbilityActivationResult.Activated, "An in-progress committed cost is idempotent.");
        };
        owner.TryActivateAbility(spec, autoCommit: false);
        owner.CommitAbility(spec);
        Check(callbacks == 1 && resource.BaseValue == 90, "Commit callbacks must not recursively pay again.");

        var cancelled = owner.GrantAbility(new GameplayAbilityDefinition(
            activationOwnedTags: new GameplayTagSet("Buff.Rage", "Debuff.Poison")));
        owner.OwnedTags.RegisterTagEventCallback("Buff.Rage", GameplayTagEventType.NewOrRemoved,
            (_, count) => { if (count != 0) owner.EndAbility(cancelled); });
        Check(owner.TryActivateAbility(cancelled, autoCommit: false) == AbilityActivationResult.Inactive &&
            !owner.OwnedTags.HasTag("Buff.Rage") && !owner.OwnedTags.HasTag("Debuff.Poison"),
            "Cancellation during tag publication must not remove unowned tags or publish later tags.");
        Console.WriteLine("PASS CommitAndActivationCallbackReentry");
    }

    private static void PeriodicExpiration()
    {
        foreach (float duration in new[] { 2f, 2.5f })
        {
            var owner = new AbilitySystemComponent();
            var resource = owner.Attributes.Add(Resource, 100);
            var effect = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration, period: 1,
                modifiers: Cost(-1).Modifiers);
            owner.Effects.Apply(new GameplayEffectSpec(effect));
            owner.Tick(100);
            Check(resource.BaseValue == 98 && owner.Effects.Count == 0,
                "A large time step must execute only periods on or before expiration.");
            owner.Effects.Apply(new GameplayEffectSpec(effect));
            owner.Tick(.5f);
            owner.Tick(1);
            owner.Tick(100);
            Check(resource.BaseValue == 96, "Split and oversized steps must agree on completed periods.");
        }
        Console.WriteLine("PASS PeriodicEffectsStopAtDurationBoundary");
    }

    private static void CommitCallbacksCanEndCast()
    {
        for (int stage = 0; stage < 3; stage++)
        {
            var router = new GameplayCueRouter();
            var owner = new AbilitySystemComponent(cueRouter: router);
            var resource = owner.Attributes.Add(Resource, 100);
            var cooldown = new GameplayEffectDefinition(EffectDurationPolicy.Duration, 5,
                tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")),
                cueTags: new GameplayTagSet("Cue.Hit.Fire"));
            var spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: Cost(), cooldown: cooldown,
                activationOwnedTags: new GameplayTagSet("Ability.Attack.Melee")));
            if (stage == 1)
                router.Register("Cue.Hit", p => { if (p.Event == GameplayCueEvent.OnActive) owner.EndAbility(spec); });
            else
                resource.OnCurrentValueChanged += (_, _, _) =>
                {
                    if (stage == 0) owner.EndAbility(spec);
                    else owner.RemoveAbility(spec);
                };
            Check(owner.TryActivateAbility(spec) == AbilityActivationResult.Inactive && !spec.IsActive,
                "Payment callbacks ending or revoking the cast must not report a live activation.");
            Check(spec.LastActivationCheck.Result == AbilityActivationResult.Inactive &&
                spec.LastCommitCheck.Result == AbilityActivationResult.Inactive,
                "Debug decisions must agree with an activation interrupted during payment.");
            Check(resource.BaseValue == 90 && owner.Effects.Count == 1 && spec.IsCommitted &&
                !owner.OwnedTags.HasTag("Ability.Attack.Melee"),
                "Already committed cost/cooldown remain after interruption; activation tags are released.");
        }
        Console.WriteLine("PASS PaymentCallbacksEndActivationWithoutSuccessOrRefund");
    }

    private static void ImmutableEffects()
    {
        var modifiers = new[] { new EffectModifier(Resource, ModifierOperation.Add, EffectMagnitude.Fixed(-1)) };
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: modifiers);
        modifiers[0] = new EffectModifier(Resource, ModifierOperation.Add, EffectMagnitude.Fixed(-1000));
        var children = new[] { new ConditionalEffect(effect, EffectTrigger.Applied) };
        var parent = new GameplayEffectDefinition(EffectDurationPolicy.Instant, conditionalEffects: children);
        children[0] = new ConditionalEffect(Cost(-1000), EffectTrigger.Applied);
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 100);
        owner.Effects.Apply(new GameplayEffectSpec(parent));
        Check(resource.BaseValue == 99, "Definition arrays must not alias mutable authoring storage.");
        Console.WriteLine("PASS EffectDefinitionOwnsImmutableConfiguration");
    }

    private static void RemovalDuringPublication()
    {
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 100);
        var other = owner.Attributes.Add(new AttributeId(2), 50);
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Infinite, modifiers: new[]
            {
                new EffectModifier(Resource, ModifierOperation.Add, EffectMagnitude.Fixed(5)),
                new EffectModifier(other.Id, ModifierOperation.Add, EffectMagnitude.Fixed(20))
            },
            tags: new EffectTags(granted: new GameplayTagSet("Buff.Rage", "Debuff.Poison")));
        resource.OnCurrentValueChanged += (_, _, current) =>
        {
            if (current == 105) owner.Effects.Remove(owner.Effects.Find(effect));
        };
        owner.Effects.Apply(new GameplayEffectSpec(effect));
        Check(owner.Effects.Count == 0 && resource.CurrentValue == 100 && other.CurrentValue == 50 && owner.OwnedTags.IsEmpty,
            "Removal from the first modifier callback must neither install later modifiers nor release ungranted tags.");

        var tagEffect = new GameplayEffectDefinition(EffectDurationPolicy.Infinite,
            tags: new EffectTags(granted: new GameplayTagSet("Buff.Rage", "Debuff.Poison")));
        owner.OwnedTags.RegisterTagEventCallback("Buff.Rage", GameplayTagEventType.NewOrRemoved,
            (_, count) => { if (count != 0) owner.Effects.Remove(owner.Effects.Find(tagEffect)); });
        owner.Effects.Apply(new GameplayEffectSpec(tagEffect));
        Check(owner.Effects.Count == 0 && owner.OwnedTags.IsEmpty,
            "Removal from a granted-tag callback must stop publishing later tags.");
        Console.WriteLine("PASS EffectRemovalDuringModifierAndTagPublication");
    }

    private static void ReplaceWithoutSelfRemoval()
    {
        var owner = new AbilitySystemComponent();
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Infinite,
            tags: new EffectTags(granted: new GameplayTagSet("Buff.Rage"), removeEffectsWith: new GameplayTagSet("Buff")));
        owner.Effects.Apply(new GameplayEffectSpec(effect), out var first);
        owner.Effects.Apply(new GameplayEffectSpec(effect), out var second);
        Check(owner.Effects.Count == 1 && !owner.Effects.TryGetInfo(first, out _) &&
            owner.Effects.TryGetInfo(second, out _) && owner.OwnedTags.GetExplicitTagCount("Buff.Rage") == 1,
            "Removing prior effects by tag must not remove the newly applied replacement.");
        Console.WriteLine("PASS ReplacementEffectsDoNotRemoveThemselves");
    }

    private static void CueClearDuringDispatch()
    {
        var router = new GameplayCueRouter();
        int original = 0, replacement = 0;
        router.Register("Cue.Hit", _ =>
        {
            original++;
            router.Clear();
            router.Register("Cue.Hit", _ => replacement++);
            router.Register("Cue.Hit", _ => replacement++);
        });
        router.Register("Cue.Hit", _ => throw new Exception("Cleared handler must not run."));
        var cue = new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, null, null, 1);
        router.Dispatch(cue);
        Check(original == 1 && replacement == 0 && router.HandlerCount == 2,
            "Handlers registered after a reentrant clear must wait for the next dispatch.");
        router.Dispatch(cue);
        Check(replacement == 2, "Replacement handlers run on the next dispatch.");
        Console.WriteLine("PASS CueClearAndRegistrationKeepDispatchSnapshot");
    }

    private static void SmallDivisors()
    {
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 1);
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            modifiers: new[] { new EffectModifier(Resource, ModifierOperation.Divide, EffectMagnitude.Fixed(.0000005f)) });
        owner.Effects.Apply(new GameplayEffectSpec(effect));
        Check(resource.BaseValue > 1000000, "A finite small divisor must not be silently ignored.");
        Console.WriteLine("PASS InstantDivisionHasNoEpsilonFallback");
    }

    private static void ManualCommitAllocations()
    {
        var owner = new AbilitySystemComponent();
        var resource = owner.Attributes.Add(Resource, 100);
        var spec = owner.GrantAbility(new GameplayAbilityDefinition(cost: Cost(), cooldown: Cooldown()));
        void Cast()
        {
            owner.TryActivateAbility(spec, autoCommit: false);
            owner.CommitAbility(spec, spec.ActivationVersion);
            owner.EndAbility(spec);
            owner.Tick(5);
            resource.BaseValue = 100;
        }
        for (int i = 0; i < 100; i++) Cast();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Cast();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "Warmed manual activation/commit/cooldown must allocate no managed memory.");
        Console.WriteLine("PERF 10000 manual activation/commit/cooldown pairs: " + allocated + " allocated bytes");
    }
}
