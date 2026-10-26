using GameplayTags;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using ZFramework.GAS;
using ZFramework.GAS.Unity;

internal static class Program
{
    private static void Main()
    {
        GameplayTagManager.Initialize(
            new GameplayTagRegistration("Ability.Attack.Melee"), new GameplayTagRegistration("Ability.AttackSpeed"),
            new GameplayTagRegistration("Buff.Rage"), new GameplayTagRegistration("Cooldown.Attack"),
            new GameplayTagRegistration("Debuff.Poison"), new GameplayTagRegistration("DebuffSlow"),
            new GameplayTagRegistration("Cue.Hit.Fire"), new GameplayTagRegistration("Cue.HitSpeed"));
        Run(nameof(UnknownNamesAreSkipped), UnknownNamesAreSkipped);
        Run(nameof(EmptyRequiredAssetRowsAreSkipped), EmptyRequiredAssetRowsAreSkipped);
        Run(nameof(Hierarchy), Hierarchy);
        Run(nameof(MultipleSources), MultipleSources);
        Run(nameof(AbilityAndEffectSources), AbilityAndEffectSources);
        Run(nameof(ContradictoryRequirements), ContradictoryRequirements);
        Run(nameof(Cooldown), Cooldown);
        Run(nameof(RemoveEffectsByParent), RemoveEffectsByParent);
        Run(nameof(CancelAbilitiesByParent), CancelAbilitiesByParent);
        Run(nameof(OngoingBlock), OngoingBlock);
        Run(nameof(CueHierarchy), CueHierarchy);
        Run(nameof(ImmutableConfiguration), ImmutableConfiguration);
        Run(nameof(CountEvents), CountEvents);
        Run(nameof(ClearReentry), ClearReentry);
        Run(nameof(ThrowingRemovalCallback), ThrowingRemovalCallback);
        Run(nameof(NameRoundTrip), NameRoundTrip);
        Run(nameof(DataContractRoundTrip), DataContractRoundTrip);
        Run(nameof(RequirementsEquivalence), RequirementsEquivalence);
        Run(nameof(RandomCountMutations), RandomCountMutations);
        HotPath();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void UnknownNamesAreSkipped()
    {
        var container = new GameplayTagContainer();
        container.SetExplicitTagsFromNames(new[] { "Buff.Rage", "Old.Removed", "", null });
        Check(container.ExplicitTagCount == 1 && !container.HasTag(GameplayTag.None), "Loading must skip unknown and empty names, never store None");
        var names = new List<string>();
        container.CopyExplicitTagNamesTo(names);
        Check(names.Count == 1 && names[0] == "Buff.Rage", "Saving must not introduce a null name");
        container.SetExplicitTagsFromNames(new[] { "Old.Removed" });
        Check(container.IsEmpty && container.TagCount == 0, "An all-unknown load replaces existing state with an empty container");
    }

    private static void EmptyRequiredAssetRowsAreSkipped()
    {
        var authored = new[]
        {
            new GameplayTag { m_Name = "" }, new GameplayTag { m_Name = "Buff.Rage" },
            default, new GameplayTag { m_Name = "Old.Removed" }
        };
        var resolved = TagAssetUtility.ToSet(authored);
        var definition = new GameplayAbilityDefinition(activationRequiredTags: resolved);
        Check(!definition.ActivationRequirements.IsImpossible && resolved.Count == 1, "Empty required rows must not create an impossible condition");
        Check(resolved[0] == (GameplayTag)"Buff.Rage", "Valid rows must resolve");
        Check(authored[0].Name == "" && authored[1].Name == "Buff.Rage" && !authored[1].IsValid && authored[3].Name == "Old.Removed", "Building must not mutate serialized tag fields");
        var asc = new AbilitySystemComponent();
        var spec = asc.GrantAbility(definition);
        Check(asc.TryActivateAbility(spec) == AbilityActivationResult.Blocked, "Nonempty required conditions still apply");
        asc.Effects.Apply(new GameplayEffectSpec(new GameplayEffectDefinition(EffectDurationPolicy.Infinite,
            tags: new EffectTags(granted: resolved))));
        Check(!asc.OwnedTags.HasTag(GameplayTag.None) && asc.OwnedTags.GetExplicitTagCount("Buff.Rage") == 1, "Granted tags must exclude invalid rows");
        Check(asc.TryActivateAbility(spec) == AbilityActivationResult.Activated, "Valid required rows permit activation");
        var empty = TagAssetUtility.ToSet(new[] { default(GameplayTag), new GameplayTag { m_Name = "" } });
        Check(empty.IsEmpty && asc.TryActivateAbility(asc.GrantAbility(new GameplayAbilityDefinition(activationRequiredTags: empty))) == AbilityActivationResult.Activated, "All-blank required rows mean no requirements");
        Check(TagAssetUtility.ToSet(null).IsEmpty && TagAssetUtility.ToSet(Array.Empty<GameplayTag>()).IsEmpty, "Null and empty asset lists remain empty");
        authored[1] = new GameplayTag { m_Name = "Debuff.Poison" };
        Check(resolved[0] == (GameplayTag)"Buff.Rage", "Built configuration remains independent of subsequent asset edits");
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static GameplayEffectDefinition Effect(string name) => new GameplayEffectDefinition(
        EffectDurationPolicy.Infinite, tags: new EffectTags(granted: new GameplayTagSet(name)));

    private static void Hierarchy()
    {
        var asc = new AbilitySystemComponent();
        asc.OwnedTags.AddTag("Ability.Attack.Melee");
        var parent = asc.GrantAbility(new GameplayAbilityDefinition(
            activationRequiredTags: new GameplayTagSet("Ability.Attack")));
        var sibling = asc.GrantAbility(new GameplayAbilityDefinition(
            activationRequiredTags: new GameplayTagSet("Ability.AttackSpeed")));
        Check(asc.TryActivateAbility(parent) == AbilityActivationResult.Activated, "Parent must match");
        Check(asc.TryActivateAbility(sibling) == AbilityActivationResult.Blocked, "Sibling must not match");
        var blocked = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            tags: new EffectTags(applicationBlocked: new GameplayTagSet("Ability.Attack")));
        Check(asc.Effects.Apply(new GameplayEffectSpec(blocked)) == EffectApplicationResult.Blocked, "Parent forbids child");
    }

    private static void MultipleSources()
    {
        var asc = new AbilitySystemComponent();
        var effect = Effect("Buff.Rage");
        asc.Effects.Apply(new GameplayEffectSpec(effect), out var a);
        asc.Effects.Apply(new GameplayEffectSpec(effect), out var b);
        asc.Effects.Remove(a);
        Check(asc.OwnedTags.HasTag("Buff.Rage"), "Second source remains");
        asc.Effects.Remove(b);
        Check(asc.OwnedTags.IsEmpty, "All sources released");
    }

    private static void AbilityAndEffectSources()
    {
        var asc = new AbilitySystemComponent();
        var spec = asc.GrantAbility(new GameplayAbilityDefinition(activationOwnedTags: new GameplayTagSet("Buff.Rage")));
        asc.TryActivateAbility(spec);
        asc.Effects.Apply(new GameplayEffectSpec(Effect("Buff.Rage")), out var handle);
        asc.EndAbility(spec);
        Check(asc.OwnedTags.HasTag("Buff.Rage"), "Effect source outlives ability");
        asc.Effects.Remove(handle);
        Check(asc.OwnedTags.IsEmpty, "Balanced owned tags");
    }

    private static void ContradictoryRequirements()
    {
        var asc = new AbilitySystemComponent();
        var spec = asc.GrantAbility(new GameplayAbilityDefinition(
            activationRequiredTags: new GameplayTagSet("Buff.Rage"), activationBlockedTags: new GameplayTagSet("Buff")));
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Instant,
            tags: new EffectTags(applicationRequired: new GameplayTagSet("Buff.Rage"), applicationBlocked: new GameplayTagSet("Buff")));
        for (int i = 0; i < 2; i++)
        {
            Check(asc.TryActivateAbility(spec) == AbilityActivationResult.Blocked, "Contradictory ability");
            Check(asc.Effects.Apply(new GameplayEffectSpec(effect)) == EffectApplicationResult.Blocked, "Contradictory effect");
            if (i == 0) asc.OwnedTags.AddTag("Buff.Rage");
        }
    }

    private static void Cooldown()
    {
        var asc = new AbilitySystemComponent();
        var cooldown = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 2,
            tags: new EffectTags(granted: new GameplayTagSet("Cooldown.Attack")));
        var spec = asc.GrantAbility(new GameplayAbilityDefinition(cooldown: cooldown));
        Check(asc.TryActivateAbility(spec) == AbilityActivationResult.Activated, "First activation");
        asc.EndAbility(spec);
        Check(asc.TryActivateAbility(spec) == AbilityActivationResult.OnCooldown, "Cooldown blocks");
        asc.Tick(2);
        Check(asc.TryActivateAbility(spec) == AbilityActivationResult.Activated, "Cooldown expires");
        asc.Clear();
        Check(asc.OwnedTags.IsEmpty && asc.Effects.Count == 0, "Clear releases cooldown");
    }

    private static void RemoveEffectsByParent()
    {
        var asc = new AbilitySystemComponent();
        asc.Effects.Apply(new GameplayEffectSpec(Effect("Debuff.Poison")));
        asc.Effects.Apply(new GameplayEffectSpec(Effect("DebuffSlow")));
        Check(asc.Effects.RemoveEffectsWithAnyTag(new GameplayTagSet("Debuff")) == 1, "Remove descendants only");
        Check(!asc.OwnedTags.HasTag("Debuff.Poison") && asc.OwnedTags.HasTag("DebuffSlow"), "Sibling effect remains");
    }

    private static void CancelAbilitiesByParent()
    {
        var asc = new AbilitySystemComponent();
        var a = asc.GrantAbility(new GameplayAbilityDefinition(abilityTags: new GameplayTagSet("Ability.Attack.Melee")));
        var b = asc.GrantAbility(new GameplayAbilityDefinition(abilityTags: new GameplayTagSet("Ability.AttackSpeed")));
        asc.TryActivateAbility(a);
        asc.TryActivateAbility(b);
        Check(asc.CancelAbilitiesWithTags(new GameplayTagSet("Ability.Attack")) == 1, "Cancel descendants only");
        Check(!a.IsActive && b.IsActive, "Sibling ability remains");
    }

    private static void OngoingBlock()
    {
        var asc = new AbilitySystemComponent();
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Infinite,
            tags: new EffectTags(granted: new GameplayTagSet("Buff.Rage"), ongoingBlocked: new GameplayTagSet("Debuff")));
        asc.Effects.Apply(new GameplayEffectSpec(effect));
        asc.Tick(0);
        Check(asc.Effects.Count == 1, "Effect remains unblocked");
        asc.OwnedTags.AddTag("Debuff.Poison");
        asc.Tick(0);
        Check(asc.Effects.Count == 0 && !asc.OwnedTags.HasTag("Buff.Rage"), "Ongoing parent block removes effect");
    }

    private static void CueHierarchy()
    {
        var router = new GameplayCueRouter();
        int hits = 0;
        router.Register("Cue.Hit", _ => hits++);
        router.Dispatch(new GameplayCueParameters("Cue.Hit.Fire", GameplayCueEvent.Executed, null, null, 1));
        router.Dispatch(new GameplayCueParameters("Cue.HitSpeed", GameplayCueEvent.Executed, null, null, 1));
        Check(hits == 1, "Existing hierarchical cue dispatch");
    }

    private static void ImmutableConfiguration()
    {
        var input = new GameplayTag[] { "Buff.Rage" };
        var set = new GameplayTagSet(input);
        input[0] = "Debuff.Poison";
        Check(set[0] == (GameplayTag)"Buff.Rage", "Configuration owns its array");
    }

    private static void HotPath()
    {
        var asc = new AbilitySystemComponent();
        asc.OwnedTags.AddTag("Buff.Rage");
        var ability = asc.GrantAbility(new GameplayAbilityDefinition(
            activationRequiredTags: new GameplayTagSet("Buff"), activationBlockedTags: new GameplayTagSet("Debuff"),
            activationOwnedTags: new GameplayTagSet("Ability.Attack.Melee")));
        for (int i = 0; i < 10000; i++) { asc.TryActivateAbility(ability); asc.EndAbility(ability); }
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100000; i++) { asc.TryActivateAbility(ability); asc.EndAbility(ability); }
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        Check(allocated == 0, "Warmed activation/tag mutation must allocate zero bytes");
        Console.WriteLine($"PERF 100000 activation/end pairs: {elapsed * 1000.0 / Stopwatch.Frequency:F2} ms; {allocated} allocated bytes");
    }

    private static void CountEvents()
    {
        var asc = new AbilitySystemComponent();
        GameplayTag parent = "Buff", child = "Buff.Rage";
        var events = new List<(GameplayTag, int)>();
        asc.OwnedTags.OnAnyTagNewOrRemove += (tag, count) => events.Add((tag, count));
        asc.OwnedTags.AddTag(child);
        asc.OwnedTags.AddTag(child);
        Check(asc.OwnedTags.GetExplicitTagCount(child) == 2 && asc.OwnedTags.GetTagCount(parent) == 2, "Count closure");
        Check(events.Count == 2 && events[0] == (parent, 1) && events[1] == (child, 1), "Ancestor and explicit transition events");
        asc.OwnedTags.RemoveTag(child);
        Check(events.Count == 2, "Intermediate removal is not a presence transition");
        asc.OwnedTags.RemoveTag(child);
        Check(events.Count == 4 && events[2] == (parent, 0) && events[3] == (child, 0), "Final removal events");
    }

    private static void ClearReentry()
    {
        var asc = new AbilitySystemComponent();
        GameplayTag rage = "Buff.Rage", poison = "Debuff.Poison";
        asc.OwnedTags.AddTag(rage);
        var events = new List<(GameplayTag, int)>();
        asc.OwnedTags.OnAnyTagNewOrRemove += (tag, count) =>
        {
            events.Add((tag, count));
            if (tag == rage && count == 0)
            {
                asc.OwnedTags.AddTag(poison);
                asc.OwnedTags.Clear();
            }
        };
        asc.OwnedTags.Clear();
        Check(asc.OwnedTags.IsEmpty, "Nested clear leaves committed empty state");
        Check(events.Count == 6 && events.Contains((poison, 1)) && events.Contains((poison, 0)), "Nested events append and drain");
        asc.OwnedTags.AddTag(poison);
        Check(asc.OwnedTags.HasTag(poison), "Container remains usable after reentry");
    }

    private static void ThrowingRemovalCallback()
    {
        var asc = new AbilitySystemComponent();
        GameplayTag rage = "Buff.Rage", poison = "Debuff.Poison";
        var effect = new GameplayEffectDefinition(EffectDurationPolicy.Infinite,
            tags: new EffectTags(granted: new GameplayTagSet(rage, poison)));
        asc.Effects.Apply(new GameplayEffectSpec(effect), out var handle);
        OnTagCountChangedDelegate callback = (tag, count) => { if (count == 0) throw new InvalidOperationException("test callback"); };
        asc.OwnedTags.RegisterTagEventCallback(rage, GameplayTagEventType.NewOrRemoved, callback);
        bool thrown = false;
        try { asc.Effects.Remove(handle); }
        catch (InvalidOperationException) { thrown = true; }
        Check(thrown && asc.OwnedTags.IsEmpty && asc.Effects.Count == 0, "Throwing callback still releases every grant");
        asc.OwnedTags.RemoveTagEventCallback(rage, GameplayTagEventType.NewOrRemoved, callback);
        asc.Effects.Apply(new GameplayEffectSpec(effect), out handle);
        asc.Effects.Remove(handle);
        Check(asc.OwnedTags.IsEmpty, "Queues and effect pool recover after callback exception");
    }

    private static void NameRoundTrip()
    {
        var container = new GameplayTagContainer();
        container.SetExplicitTagsFromNames(new[] { "Debuff.Poison", "Buff.Rage", "Buff.Rage" });
        var names = new List<string> { "stale" };
        container.CopyExplicitTagNamesTo(names);
        Check(names.Count == 2 && names[0] == "Buff.Rage" && names[1] == "Debuff.Poison", "Sorted distinct explicit names");
        Check(container.HasTag("Buff") && !container.HasTagExact("Buff"), "Names restore implicit ancestors");
        var indices = container.Indices;
        container.SetExplicitTagsFromNames(new[] { "Cooldown.Attack" });
        Check(indices.ExplicitTagCount == 1 && container.HasTag("Cooldown") && !container.HasTag("Buff"), "Loading reuses storage and replaces state");
        container.SetExplicitTagsFromNames(Array.Empty<string>());
        Check(container.IsEmpty && indices.TagCount == 0, "Empty name load clears closure");
    }

    private static void DataContractRoundTrip()
    {
        var original = new GameplayTagContainer();
        original.AddTag("Buff.Rage");
        original.AddTag("Debuff.Poison");
        using var stream = new MemoryStream();
        var serializer = new DataContractSerializer(typeof(GameplayTagContainer));
        serializer.WriteObject(stream, original);
        stream.Position = 0;
        var restored = (GameplayTagContainer)serializer.ReadObject(stream);
        Check(restored.ExplicitTagCount == 2 && restored.HasTag("Buff") && !restored.HasTagExact("Buff"), "DataContract preserves explicit set and closure");
        using var tagStream = new MemoryStream();
        serializer = new DataContractSerializer(typeof(GameplayTag));
        serializer.WriteObject(tagStream, (GameplayTag)"Buff.Rage");
        tagStream.Position = 0;
        Check((GameplayTag)serializer.ReadObject(tagStream) == (GameplayTag)"Buff.Rage", "DataContract resolves standalone tag");
    }

    private static void RequirementsEquivalence()
    {
        GameplayTag[] candidates = { "Buff", "Buff.Rage", "Debuff", "Debuff.Poison", "Ability.Attack", "Ability.Attack.Melee" };
        var holder = new GameplayTagCountContainer();
        var required = new List<GameplayTag>();
        var blocked = new List<GameplayTag>();
        for (int rule = 0; rule < 128; rule++)
        {
            required.Clear(); blocked.Clear();
            for (int bit = 0; bit < candidates.Length; bit++)
            {
                if ((rule & (1 << bit)) != 0) required.Add(candidates[bit]);
                if (((rule * 37) & (1 << bit)) != 0) blocked.Add(candidates[bit]);
            }
            var definition = new GameplayAbilityDefinition(activationRequiredTags: new GameplayTagSet(required), activationBlockedTags: new GameplayTagSet(blocked));
            for (int state = 0; state < 64; state++)
            {
                holder.Clear();
                for (int bit = 0; bit < candidates.Length; bit++)
                    if ((state & (1 << bit)) != 0) holder.AddTag(candidates[bit]);
                bool expected = true;
                foreach (var tag in required) expected &= holder.HasTag(tag);
                foreach (var tag in blocked) expected &= !holder.HasTag(tag);
                Check(definition.ActivationRequirements.Matches(holder) == expected, "Compiled requirements preserve matching semantics");
            }
        }
    }

    private static void RandomCountMutations()
    {
        var holder = new GameplayTagCountContainer();
        var all = GameplayTagManager.GetAllTags().ToArray();
        var explicitCounts = new int[all.Length];
        var random = new Random(5137);
        OnTagCountChangedDelegate listener = (tag, count) => { };
        for (int step = 0; step < 20000; step++)
        {
            if (step % 100 == 0) holder.OnAnyTagCountChange += listener;
            if (step % 100 == 50) holder.OnAnyTagCountChange -= listener;
            int index = random.Next(all.Length);
            if (step % 197 == 0)
            {
                holder.Clear();
                Array.Clear(explicitCounts, 0, explicitCounts.Length);
            }
            else if (explicitCounts[index] == 0 || random.Next(2) == 0)
            {
                int amount = random.Next(1, 4);
                holder.AddTag(all[index], amount);
                explicitCounts[index] += amount;
            }
            else
            {
                int amount = random.Next(1, explicitCounts[index] + 1);
                holder.RemoveTag(all[index], amount);
                explicitCounts[index] -= amount;
            }
            int explicitTotal = 0, total = 0;
            for (int i = 0; i < all.Length; i++)
            {
                int expected = 0;
                for (int j = 0; j < all.Length; j++)
                    if (all[i] == all[j] || all[i].IsParentOf(all[j])) expected += explicitCounts[j];
                Check(holder.GetExplicitTagCount(all[i]) == explicitCounts[i], "Explicit count after mixed mutations");
                Check(holder.GetTagCount(all[i]) == expected, "Ancestor total after mixed mutations");
                if (explicitCounts[i] != 0) explicitTotal++;
                if (expected != 0) total++;
            }
            Check(holder.ExplicitTagCount == explicitTotal && holder.TagCount == total, "Dense storage totals");
        }
    }
}
