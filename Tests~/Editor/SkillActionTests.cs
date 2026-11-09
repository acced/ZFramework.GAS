using System;
using System.Collections.Generic;
using GameplayTags;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SkillActionTestUnit : MonoBehaviour, IAbilitySystemProvider
    {
        public AbilitySystemComponent AbilitySystem { get; } = new();
        private void OnDestroy() => AbilitySystem.Clear();
    }
    public sealed class SkillActionTests
    {
        private readonly List<Object> _objects = new();
        [SetUp] public void Setup() => TestTags.Initialize();
        [TearDown] public void Cleanup()
        {
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear(); Physics.SyncTransforms();
        }
        private T New<T>() where T : ScriptableObject
        { var value = ScriptableObject.CreateInstance<T>(); _objects.Add(value); return value; }
        private SkillActionTestUnit Unit(string name, Vector3 position)
        {
            var go = new GameObject(name); go.transform.position = position; _objects.Add(go);
            go.AddComponent<SphereCollider>().radius = .2f;
            var unit = go.AddComponent<SkillActionTestUnit>(); unit.AbilitySystem.Attributes.Add(new AttributeId(1), 100); return unit;
        }
        private static void Edit(ScriptableObject asset, Action<SerializedObject> edit)
        { using var so = new SerializedObject(asset); edit(so); so.ApplyModifiedPropertiesWithoutUndo(); }
        private GameplayAbilityAsset Skill(params SkillAction[] actions)
        { var skill = New<GameplayAbilityAsset>(); skill.activationActions = actions; return skill; }
        private GameplayEffectAsset Effect(float amount = -10, string parameter = null)
        {
            var effect = New<GameplayEffectAsset>();
            Edit(effect, so =>
            {
                var list = so.FindProperty("modifiers"); list.arraySize = 1; var item = list.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("targetAttributeId").intValue = 1;
                item.FindPropertyRelative("magnitude.constant").floatValue = parameter == null ? amount : 0;
                if (parameter != null)
                {
                    item.FindPropertyRelative("magnitude.kind").intValue = (int)MagnitudeKind.Parameter;
                    item.FindPropertyRelative("magnitude.parameterId").stringValue = parameter;
                    item.FindPropertyRelative("magnitude.coefficient").floatValue = -1;
                }
            });
            return effect;
        }
        [Serializable] private sealed class CaptureAction : SkillAction
        {
            public Action<SkillExecutionContext> Callback;
            public override SkillActionDefinition Build(SkillActionBuildContext builder) => Define("Capture", Callback);
        }
        private sealed class Board : ISkillTargetSource
        {
            public AbilitySystemComponent[] Units;
            public void Collect(string key, SkillExecutionContext context, SkillTargetResults results)
            { foreach (var unit in Units) results.Add(unit); }
        }
        [Test] public void TurnBasedCastHitsChosenLogicalUnitWithoutSceneOrTimeline()
        {
            var owner = new AbilitySystemComponent(); var chosen = new AbilitySystemComponent();
            chosen.Attributes.Add(new AttributeId(1), 100);
            var skill = Skill(new ApplyEffectAction { effect = Effect(), targets = new TargetQuery {
                kind = TargetQueryKind.FixedTarget, radius = new SkillNumber { parameterId = "unused geometry" }, origin = SkillOrigin.EventPosition } });
            var runner = new SkillRunner(owner); runner.Grant(skill);
            runner.TryCast(skill); Assert.AreEqual(100, chosen.Attributes.GetCurrent(new AttributeId(1)));
            runner.TryCast(skill, new SkillCastTarget(chosen)); Assert.AreEqual(90, chosen.Attributes.GetCurrent(new AttributeId(1)));
        }

        [Test] public void RepeatedValueContextsStayWarmWithoutKeepingTemporaryContextsAlive()
        {
            var p = new SkillParameter { defaultValue = 1 };
            var skill = Skill(new ApplyEffectAction { effect = Effect(parameter: p.Id), targets = new TargetQuery { kind = TargetQueryKind.Self } });
            skill.parameters = new[] { p };
            var owner = new AbilitySystemComponent();
            var health = owner.Attributes.Add(new AttributeId(1), 100);
            var runner = new SkillRunner(owner); runner.Grant(skill);
            var fixedValues = new EffectEvaluationContext(1, skill.ResolveParameters(1, new[] { new SkillParameterOverride { parameterId = p.Id, value = 7 } }));
            var temporary = new WeakReference[64];
            for (int i = 0; i < temporary.Length; i++) temporary[i] = CastOnce(runner, skill, p.Id, i);
            var otherValues = new EffectEvaluationContext(1, skill.ResolveParameters(1, new[] { new SkillParameterOverride { parameterId = p.Id, value = 3 } }));
            GC.Collect();
            Assert.Less(Array.FindAll(temporary, r => r.IsAlive).Length, 8, "The runner must not own one-off value contexts.");
            runner.TryCast(skill, values: fixedValues); runner.TryCast(skill, values: otherValues);
            Assert.IsTrue(AllocationMeter.Works());
            using var meter = new AllocationMeter();
            health.BaseValue = 5093;
            for (int i = 0; i < 1000; i++) runner.TryCast(skill, values: i % 2 == 0 ? fixedValues : otherValues);
            long allocations = meter.Stop();
            Assert.AreEqual(0, allocations);
            Assert.AreEqual(93, health.CurrentValue);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference CastOnce(SkillRunner runner, GameplayAbilityAsset skill, string id, int value)
        {
            var overrides = new[] { new SkillParameterOverride { parameterId = id, value = value } };
            var values = new EffectEvaluationContext(1, skill.ResolveParameters(1, overrides));
            runner.TryCast(skill, values: values);
            return new WeakReference(values);
        }

        [Test] public void WaitEventCatchesSameCastMessageAndCancellationUnsubscribes()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var wait = new WaitSkillAction { kind = SkillWaitKind.GameplayEvent, eventTag = "Test.Other", timeout = new SkillNumber(2),
                onSuccess = new SkillAction[] { new ApplyEffectAction { effect = Effect(), targets = new TargetQuery { kind = TargetQueryKind.Self } } } };
            var skill = Skill(wait, new SendGameplayEventAction { tag = "Test.Other" }); var runner = new SkillRunner(owner, null);
            runner.Grant(skill);
            runner.TryCast(skill); owner.Tick(.1f); owner.Tick(.1f);
            Assert.AreEqual(90, owner.Attributes.GetCurrent(new AttributeId(1))); Assert.IsFalse(runner.GetSpec(skill).IsActive);
            skill.activationActions = new SkillAction[] { wait }; skill.InvalidateDefinition(); runner.TryCast(skill); owner.EndAbility(runner.GetSpec(skill));
            owner.Events.Send(new GameplayEvent("Test.Other", owner, new SkillParameterValue(0))); owner.Tick(3);
            Assert.AreEqual(90, owner.Attributes.GetCurrent(new AttributeId(1)));
        }

        [Test] public void CancelDuringSynchronousActionStopsFollowingActions()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var skill = Skill(new SendGameplayEventAction { tag = "Test.Other" },
                new ApplyEffectAction { effect = Effect(), targets = new TargetQuery { kind = TargetQueryKind.Self } });
            var runner = new SkillRunner(owner, null);
            owner.Events.Received += _ => owner.EndAbility(runner.GetSpec(skill));
            runner.Grant(skill);
            runner.TryCast(skill);
            Assert.AreEqual(100, owner.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(0, owner.ActiveTaskCount);
        }

        [Test] public void EditingParametersAndDefinitionsOnlyChangesNextCast()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var parameter = new SkillParameter { displayName = "伤害", defaultValue = 10 };
            var effect = Effect(parameter: parameter.Id);
            var wait = new WaitSkillAction { timeout = new SkillNumber(.1f), onSuccess = new SkillAction[] { new ApplyEffectAction { effect = effect, targets = new TargetQuery { kind = TargetQueryKind.Self } } } };
            var skill = Skill(wait); skill.parameters = new[] { parameter };
            var cost = Effect(-1); Edit(skill, so => so.FindProperty("cost").objectReferenceValue = cost);
            var runner = new SkillRunner(owner, null);runner.Grant(skill);  runner.TryCast(skill);
            parameter.defaultValue = 20; Edit(cost, so => so.FindProperty("modifiers.Array.data[0].magnitude.constant").floatValue = -2);
            cost.InvalidateDefinition(); skill.InvalidateDefinition(); owner.Tick(.2f); owner.Tick(0);
            Assert.AreEqual(89, owner.Attributes.GetCurrent(new AttributeId(1)));
            runner.TryCast(skill); owner.Tick(.2f); owner.Tick(0);
            Assert.AreEqual(67, owner.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void SelectedTargetsKeepBranchTargetsAndDoNotMutateTheCaster()
        {
            var owner = new AbilitySystemComponent(); var a = new AbilitySystemComponent(); var b = new AbilitySystemComponent();
            var seen = new List<AbilitySystemComponent>();
            var services = new SkillUnitServices(); services.Set<ISkillTargetSource>(new Board { Units = new[] { a, b } });
            var skill = Skill(new SelectTargetsAction {
                targets = new TargetQuery { kind = TargetQueryKind.Project, sourceKey = "row" },
                actions = new SkillAction[] { new CaptureAction { Callback = c => { Assert.AreSame(owner, c.Source); seen.Add(c.Target); } } }
            }, new CaptureAction { Callback = c => Assert.IsNull(c.Target) });
            var runner = new SkillRunner(owner, services); runner.Grant(skill); runner.TryCast(skill);
            CollectionAssert.AreEqual(new[] { a, b }, seen);
        }
        [Test] public void RecycledCandidateIsSkippedAfterAnEarlierBranchChangesItsGeneration()
        {
            var owner = new AbilitySystemComponent(); var a = new AbilitySystemComponent(); var b = new AbilitySystemComponent();
            var services = new SkillUnitServices(); services.Set<ISkillTargetSource>(new Board { Units = new[] { a, b } });
            int count = 0;
            var skill = Skill(new SelectTargetsAction {
                targets = new TargetQuery { kind = TargetQueryKind.Project, sourceKey = "row" },
                actions = new SkillAction[] { new CaptureAction { Callback = c => { count++; b.Clear(); } } }
            });
            var runner = new SkillRunner(owner, services); runner.Grant(skill); runner.TryCast(skill);
            Assert.AreEqual(1, count);
        }
        [Test] public void TargetInputDoesNotLeakBetweenWarmCasts()
        {
            var unit = Unit("source", Vector3.one); var target = Unit("target", Vector3.forward);
            SkillExecutionContext captured = null;
            var skill = Skill(new CaptureAction { Callback = c => captured = c });
            var runner = new SkillRunner(unit.AbilitySystem, new SkillUnitServices { Origin = unit.transform }); runner.Grant(skill);
            runner.TryCast(skill, new SkillCastTarget(target.AbilitySystem, target.transform, Vector3.zero));
            Assert.AreEqual(Vector3.zero, captured.Services.RequireTargetPosition());
            var logical = captured.WithTarget(unit.AbilitySystem);
            Assert.IsFalse(logical.Services.HasTargetPosition);
            Assert.AreEqual(Vector3.zero, captured.Services.RequireTargetPosition());
            runner.TryCast(skill);
            Assert.IsNull(captured.Target); Assert.IsNull(captured.Services.Target); Assert.IsFalse(captured.Services.HasTargetPosition);
            Assert.AreSame(unit.transform, captured.Services.Origin);
        }
        [Test] public void TargetQueriesDeduplicateSortAndKeepNestedBuffersIndependent()
        {
            var source = Unit("source", Vector3.zero); var near = Unit("near", Vector3.forward); var far = Unit("far", Vector3.forward * 2);
            near.gameObject.AddComponent<BoxCollider>();
            var rejected = Unit("rejected", Vector3.forward * .5f); rejected.AbilitySystem.OwnedTags.AddTag("Test.Other");
            Physics.SyncTransforms();
            var skill = Skill(new CaptureAction { Callback = context => {
                var outer = context.RentBuffer(); var inner = context.RentBuffer();
                try
                {
                    var query = new TargetQuery { kind = TargetQueryKind.Sector, blockedTags = new[] { (GameplayTag)"Test.Other" }, maxTargets = 2 }.Build();
                    context.Services.Searcher.Query(query, context, outer);
                    Assert.AreEqual(2, outer.Count); Assert.AreSame(near.AbilitySystem, outer.Units[0]); Assert.AreSame(far.AbilitySystem, outer.Units[1]);
                    context.Services.Searcher.Query(new TargetQuery { kind = TargetQueryKind.Self }.Build(), context, inner);
                    Assert.AreSame(near.AbilitySystem, outer.Units[0]); Assert.AreSame(source.AbilitySystem, inner.Units[0]);
                }
                finally { context.ReturnBuffer(inner); context.ReturnBuffer(outer); }
            } });
            var runner = new SkillRunner(source.AbilitySystem, new SkillUnitServices { Origin = source.transform, Searcher = new PhysicsTargetSearcher() });
            runner.Grant(skill); runner.TryCast(skill);
        }
        private sealed class ImmediateCancelSelection : ISkillTargetSelection, IDisposable
        {
            public int Disposed;
            public IDisposable Request(SkillExecutionContext context, Action<AbilitySystemComponent, Transform> selected)
            { context.Source.EndAbility(context.Ability); return this; }
            public void Dispose() => Disposed++;
        }
        [Test] public void TargetSelectionCancelledInsideRequestDisposesReturnedHandle()
        {
            var owner = new AbilitySystemComponent(); var service = new ImmediateCancelSelection();
            var skill = Skill(new WaitSkillAction { kind = SkillWaitKind.Target });
            var runner = new SkillRunner(owner, new SkillUnitServices { TargetSelection = service }); runner.Grant(skill); runner.TryCast(skill);
            Assert.AreEqual(1, service.Disposed); Assert.AreEqual(0, owner.ActiveTaskCount);
        }
        [Test] public void ClearRequiresExplicitRegrantAndRevokeRemovesBookkeeping()
        {
            var owner = new AbilitySystemComponent(); var skill = Skill(); var runner = new SkillRunner(owner);
            var first = runner.Grant(skill); owner.Clear();
            Assert.AreEqual(AbilityActivationResult.Inactive, runner.TryCast(skill).Result);
            Assert.IsNull(runner.GetSpec(skill)); Assert.IsTrue(runner.Revoke(skill));
            var next = runner.Grant(skill, 3); Assert.AreNotSame(first, next);
            Assert.AreEqual(3, next.Level); Assert.IsTrue(runner.TryCast(skill).Succeeded);
            Assert.IsTrue(runner.Revoke(skill)); Assert.AreEqual(0, owner.GrantedAbilityCount);
        }
        [Test] public void PreparedValuesAreImmutableAndDoNotChangeTheGrantDefaults()
        {
            var parameter = new SkillParameter { levels = new[] { 1f, 2f } }; var owner = new AbilitySystemComponent();
            var health = owner.Attributes.Add(new AttributeId(1), 100);
            var skill = Skill(new ApplyEffectAction { effect = Effect(parameter: parameter.Id), targets = new TargetQuery { kind = TargetQueryKind.Self } });
            skill.parameters = new[] { parameter };
            var runner = new SkillRunner(owner); runner.Grant(skill, 2);
            var overrides = new[] { new SkillParameterOverride { parameterId = parameter.Id, value = 7 } };
            var first = new EffectEvaluationContext(2, skill.ResolveParameters(2, overrides));
            overrides[0].value = 3;
            runner.TryCast(skill, values: first); Assert.AreEqual(93, health.CurrentValue);
            var next = new EffectEvaluationContext(2, skill.ResolveParameters(2, overrides));
            runner.TryCast(skill, values: next); Assert.AreEqual(90, health.CurrentValue);
            runner.TryCast(skill); Assert.AreEqual(88, health.CurrentValue);
        }
        private sealed class RecyclingFilter : ITargetFilter
        {
            public bool Allows(AbilitySourceIdentity source, AbilitySystemComponent target)
            { target.Clear(); return true; }
        }
        [Test] public void FilterCannotAdmitARecycledCandidateFromAnOlderQuery()
        {
            var source = new AbilitySystemComponent(); var target = new AbilitySystemComponent(); int count = 0;
            var services = new SkillUnitServices { TargetFilter = new RecyclingFilter() };
            services.Set<ISkillTargetSource>(new Board { Units = new[] { target } });
            var skill = Skill(new SelectTargetsAction {
                targets = new TargetQuery { kind = TargetQueryKind.Project, sourceKey = "row" },
                actions = new SkillAction[] { new CaptureAction { Callback = _ => count++ } }
            });
            var runner = new SkillRunner(source, services); runner.Grant(skill); runner.TryCast(skill);
            Assert.AreEqual(0, count);
        }
        [Test] public void BooleanBranchesAndDetachedCallbacksEnforceTheirContractsAtBuildTime()
        {
            var number = new SkillParameter(); var branch = new ConditionalAction { booleanParameterId = number.Id };
            var skill = Skill(branch); skill.parameters = new[] { number };
            Assert.AreEqual(branch.Id, Assert.Throws<SkillActionException>(() => skill.BuildActions()).ActionId);
            Assert.Throws<SkillOwnershipException>(() => new SkillActionBuildContext().BuildDetached(new SkillAction[] { new WaitSkillAction() }));
            Assert.Throws<SkillOwnershipException>(() => new SkillActionBuildContext().BuildDetached(new SkillAction[] { new CommitAbilityAction() }));
        }
        [Test] public void EventsStopAfterFirstReceiverCancelsCast()
        {
            var owner = new AbilitySystemComponent(); var a = new AbilitySystemComponent(); var b = new AbilitySystemComponent();
            var services = new SkillUnitServices(); services.Set<ISkillTargetSource>(new Board { Units = new[] { a, b } });
            var skill = Skill(new SendGameplayEventAction { tag = "Test.Other", targets = new TargetQuery { kind = TargetQueryKind.Project, sourceKey = "row" } });
            var runner = new SkillRunner(owner, services); var spec = runner.Grant(skill); int received = 0;
            a.Events.Received += _ => { received++; owner.EndAbility(spec); }; b.Events.Received += _ => received++;
            runner.TryCast(skill); Assert.AreEqual(1, received);
        }
    }
}
