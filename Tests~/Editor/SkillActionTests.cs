using System;
using System.Collections.Generic;
using GameplayTags;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SkillActionTestUnit : MonoBehaviour, IAbilitySystemProvider
    {
        public AbilitySystemComponent AbilitySystem { get; } = new AbilitySystemComponent();
    }
    public sealed class SkillCancelInitializer : MonoBehaviour, ISkillSpawnInitializer
    { public void InitializeSpawn(SkillExecutionContext context) => context.Source.EndAbility(context.Ability); }
    public sealed class SkillActionTests
    {
        private readonly List<Object> _objects = new();
        private readonly GameplayTaskHost _scene = new();
        private sealed class Driver : IAnimationDriver
        { public float Play(string name, float duration, bool loop) => .2f; public void Stop() { } public void Advance(float dt) { } }
        private sealed class Pool : ISkillObjectService
        {
            public int Created, Returned;
            public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent)
            { Created++; var instance = Object.Instantiate(prefab, position, rotation, parent); instance.SetActive(true); return instance; }
            public void Release(GameObject instance) { Returned++; if (instance != null) Object.DestroyImmediate(instance); }
        }
        [SetUp] public void Setup() => TestTags.Initialize();
        [TearDown] public void Cleanup()
        {
            _scene.Clear();
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear(); Physics.SyncTransforms();
        }
        private T New<T>() where T : ScriptableObject
        { var value = ScriptableObject.CreateInstance<T>(); _objects.Add(value); return value; }
        private GameObject Go(string name, Vector3 position = default)
        { var value = new GameObject(name); value.transform.position = position; _objects.Add(value); return value; }
        private SkillActionTestUnit Unit(string name, Vector3 position)
        {
            var go = Go(name, position); go.AddComponent<SphereCollider>().radius = .2f;
            var unit = go.AddComponent<SkillActionTestUnit>(); unit.AbilitySystem.Attributes.Add(new AttributeId(1), 100); return unit;
        }
        private static void Edit(ScriptableObject asset, Action<SerializedObject> edit)
        { using var so = new SerializedObject(asset); edit(so); so.ApplyModifiedPropertiesWithoutUndo(); }
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
                    item.FindPropertyRelative("magnitude.kind").enumValueIndex = (int)MagnitudeKind.Parameter;
                    item.FindPropertyRelative("magnitude.parameterId").stringValue = parameter;
                    item.FindPropertyRelative("magnitude.coefficient").floatValue = -1;
                }
            });
            return effect;
        }
        private GameplayAbilityAsset Skill(params SkillAction[] actions)
        { var skill = New<GameplayAbilityAsset>(); skill.activationActions = actions; return skill; }
        private TimelineTaskContext Services(Transform origin, Transform target = null, Pool pool = null)
            => new TimelineTaskContext { Origin = origin, Target = target, TargetPosition = target != null ? target.position : Vector3.forward * 3,
                Objects = pool ?? new Pool(), Searcher = new TargetSearcher(), SceneTasks = _scene };

        [Test] public void GeometryPreviewMatchesRuntimeSphereColliderQueriesIncludingSurfaceContacts()
        {
            var origin=Unit("origin",new Vector3(333,0,333));var dummy=Unit("proxy",origin.transform.position);
            float radius=SkillPreviewSimulation.DummyRadius;var collider=dummy.GetComponent<SphereCollider>();
            collider.radius=radius;collider.center=Vector3.up*radius;dummy.gameObject.layer=29;
            var ctx=new SkillExecutionContext(null,origin.AbilitySystem.GrantAbility(new GameplayAbilityDefinition()),null,new EffectEvaluationContext(1),Services(origin.transform));
            var buffer=new TargetQueryBuffer();var searcher=new TargetSearcher();
            foreach(var kind in new[]{TargetQueryKind.Circle,TargetQueryKind.Sector,TargetQueryKind.Line})
            {
                var query=new TargetQuery{kind=kind,radius=new SkillNumber(3),width=new SkillNumber(1),layerMask=1<<29}.Build();
                bool hit=false,miss=false;
                foreach(var delta in new[]{new Vector3(0,0,3.10f),new Vector3(0,0,3.22f),new Vector3(.62f,0,2),new Vector3(.75f,0,2),new Vector3(1.9f,0,.2f)})
                {
                    dummy.transform.position=origin.transform.position+delta;Physics.SyncTransforms();
                    bool actual=searcher.Query(query,ctx,buffer)>0;
                    Assert.AreEqual(actual,SkillPreviewSimulation.Contains(query,ctx.Values,delta,radius),kind+" at "+delta);
                    hit|=actual;miss|=!actual;
                }
                Assert.IsTrue(hit && miss);
            }
        }
        [Test] public void PreviewOfAnimationlessCastUsesOneConditionalBranchAndTheAimedProjectileContact()
        {
            var owner=Unit("caster",new Vector3(88,0,88));var points=new SkillActionTestUnit[3];
            for(int i=0;i<points.Length;i++){points[i]=Unit("proxy"+i,owner.transform.position+SkillPreviewSimulation.Position(DummyLayout.直线,i,3,2));
                var c=points[i].GetComponent<SphereCollider>();c.radius=SkillPreviewSimulation.DummyRadius;c.center=Vector3.up*c.radius;
                points[i].gameObject.layer=28;}
            Physics.SyncTransforms();
            var hit=new ApplyEffectAction{effect=Effect(),targets=new TargetQuery{kind=TargetQueryKind.FixedTarget}};
            var skill=Skill(new ConditionalAction{requireTarget=true,whenTrue=new SkillAction[]{
                new LaunchProjectileAction{prefab=Go("missile"),speed=new SkillNumber(4),radius=.12f,onHit=new SkillAction[]{hit}}},
                whenFalse=new SkillAction[]{new ApplyEffectAction{effect=Effect(),targets=new TargetQuery{kind=TargetQueryKind.Circle,radius=new SkillNumber(3)}}}});
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASPreviewBranch.asset");AssetDatabase.CreateAsset(skill,path);
            var preview=new PreviewRenderUtility();
            try{
                var runner=new SkillRunner(owner.AbilitySystem,null);runner.TryCast(skill,points[2].AbilitySystem,Services(owner.transform,points[2].transform));
                _scene.Tick(.46f);_scene.Tick(.04f); int runtime=0;
                for(int i=0;i<3;i++)if(points[i].AbilitySystem.Attributes.GetCurrent(new AttributeId(1))<100){runtime++;Assert.AreEqual(2,i);}
                Assert.AreEqual(1,runtime);
                using var doc=new SkillDocument();doc.Open(skill);doc.Playback.Seek(.5f,1);
                using var simulation=new SkillPreviewSimulation(preview){AimIndex=3};
                simulation.Sample(doc,owner.transform,3,DummyLayout.直线,2,null,1,false,null);
                Assert.AreEqual(runtime,simulation.HitCount);Assert.IsTrue(simulation.WasHit(2));
                doc.Playback.Seek(0,1);simulation.AimIndex=4;
                simulation.Sample(doc,owner.transform,4,DummyLayout.直线,2,null,1,false,null);
                Assert.AreEqual(0,simulation.HitCount,"Aimed projectile has not arrived; the alternate area must not fire.");
                simulation.AimIndex=0;simulation.Sample(doc,owner.transform,3,DummyLayout.直线,2,null,1,false,null);
                Assert.AreEqual(3,simulation.HitCount,"No target must select only the alternate branch.");
            }finally{preview.Cleanup();AssetDatabase.DeleteAsset(path);}
        }
        [Test] public void EffectOnlyParameterSurvivesModuleExtractionRemappingAndInlining()
        {
            var p=new SkillParameter{defaultValue=7};var alternate=new SkillParameter{defaultValue=19};
            var effect=Effect(parameter:p.Id);
            var root=Skill(new ApplyEffectAction{effect=effect,targets=new TargetQuery{kind=TargetQueryKind.Self}});root.parameters=new[]{p,alternate};
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectScopeTest.asset");
            var modulePath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectScopeModule.asset");
            AssetDatabase.CreateAsset(root,path);AssetDatabase.AddObjectToAsset(effect,root);
            try
            {
                using var doc=new SkillDocument();doc.Open(root);
                float Cast(){
                    var asc=new AbilitySystemComponent();asc.Attributes.Add(new AttributeId(1),100);
                    var runner=new SkillRunner(asc,null);runner.TryCast(root);return asc.Attributes.GetCurrent(new AttributeId(1));
                }
                Assert.AreEqual(93,Cast());
                SkillFlowCommands.Extract(doc,root,"activationActions",0,1,modulePath,_=>true);
                var call=(CallModuleAction)root.activationActions[0];Assert.AreEqual(p.Id,call.bindings[0].callerParameterId);
                Assert.AreEqual(93,Cast());call.bindings[0].callerParameterId=alternate.Id;
                Assert.AreEqual(81,Cast()); SkillFlowCommands.Inline(doc,root,"activationActions",0,_=>true);
                Assert.IsInstanceOf<InlineParameterScopeAction>(root.activationActions[0]);Assert.AreEqual(81,Cast());
                var owned=((ApplyEffectAction)((InlineParameterScopeAction)root.activationActions[0]).actions[0]).effect;
                Assert.AreNotSame(((ApplyEffectAction)call.module.actions[0]).effect,owned);
                Assert.AreEqual(path,AssetDatabase.GetAssetPath(owned));
                AssetDatabase.DeleteAsset(modulePath);Assert.AreEqual(81,Cast(),"Inlined effects must outlive the module file.");
                CollectionAssert.DoesNotContain(AssetDatabase.GetDependencies(path),modulePath);
            }finally{AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(modulePath);}
        }

        private sealed class RecordAim : SkillAction
        {
            public readonly List<Vector3> Points = new(2048);
            public override SkillActionDefinition Build(SkillActionBuildContext builder)
                => Define("record", null, context => Points.Add(context.Services.RequireTargetPosition()));
        }
        [Test] public void ParameterEventAndRepeatedModuleScopesPreserveExplicitAimWithoutPeriodicAllocations()
        {
            var owner = Unit("owner", Vector3.zero); var target = Unit("target", Vector3.forward * 9);
            var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(100) });
            var runner = new SkillRunner(owner.AbilitySystem, null); var services = Services(owner.transform, target.transform);
            services.TargetPosition = Vector3.right * 7; runner.TryCast(skill, target.AbilitySystem, services);
            var context = runner.GetExecution(skill);
            Assert.AreEqual(services.TargetPosition, context.WithParameters(SkillParameterValues.Empty).Services.TargetPosition);
            Assert.AreEqual(services.TargetPosition, context.WithEvent(default).Services.TargetPosition);
            var spy = new RecordAim(); var module = New<SkillActionModuleAsset>(); module.actions = new SkillAction[] { spy };
            var action = new CallModuleAction { module = module }.Build(new SkillActionBuildContext());
            action.Validate(context); context.ExecuteAction(action);
            context.Services.TargetPosition = Vector3.left * 4; context.ExecuteAction(action);
            CollectionAssert.AreEqual(new[] { Vector3.right * 7, Vector3.left * 4 }, spy.Points);
            Assert.IsTrue(AllocationMeter.Works()); using var meter = new AllocationMeter();
            for (int i = 0; i < 1000; i++) context.ExecuteAction(action);
            long count = meter.Stop(); Assert.AreEqual(0, count, "Warm synchronous module scopes must reuse their leased frames.");
            runner.Cancel(skill);
        }
        [Test] public void CancelActionFailureIsIsolatedAndDoesNotEscapeEndAbility()
        {
            var owner = Unit("owner", default); var target = Unit("target", Vector3.forward); var prefab = Go("prefab"); var pool = new Pool();
            var invalidAtCancel = new SpawnPrefabAction { prefab = prefab, position = SkillSpawnPosition.Target };
            var skill = Skill(new SpawnPrefabAction { prefab = prefab }, new WaitSkillAction {
                timeout = new SkillNumber(100), onCancel = new SkillAction[] { new SpawnPrefabAction { prefab = prefab }, invalidAtCancel } });
            var runner = new SkillRunner(owner.AbilitySystem, null); runner.TryCast(skill, target.AbilitySystem, Services(owner.transform, target.transform, pool));
            Assert.AreEqual(1, _scene.Count); Object.DestroyImmediate(target.gameObject);
            Assert.DoesNotThrow(() => runner.Cancel(skill));
            Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(1, _scene.Count, "Earlier scene tasks survive; cleanup-created tasks are returned on failure.");
            Assert.AreEqual(2, pool.Created); Assert.AreEqual(1, pool.Returned);
            Assert.IsNull(runner.GetExecution(skill).Error); Assert.IsTrue(runner.GetSpec(skill).LastExecutionFailure.Succeeded);
            Assert.IsNotEmpty(runner.GetSpec(skill).LastCleanupError); Assert.AreEqual(invalidAtCancel.Id, runner.GetSpec(skill).LastCleanupActionId);
            _scene.Tick(6); Assert.AreEqual(2, pool.Returned);
        }
        [Test] public void ModuleWaitCapturesAimBeforeReusableModuleFrameChanges()
        {
            var owner = Unit("owner", default); var runner = new SkillRunner(owner.AbilitySystem, null);
            var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(100) }); runner.TryCast(skill, taskContext: Services(owner.transform));
            var context = runner.GetExecution(skill); var spy = new RecordAim(); var module = New<SkillActionModuleAsset>();
            module.actions = new SkillAction[] { new WaitSkillAction { onSuccess = new SkillAction[] { spy } } };
            var action = new CallModuleAction { module = module }.Build(new SkillActionBuildContext());
            context.Services.TargetPosition = Vector3.left; context.ExecuteAction(action);
            context.Services.TargetPosition = Vector3.right; context.ExecuteAction(action);
            owner.AbilitySystem.Tick(1); CollectionAssert.AreEquivalent(new[] { Vector3.left, Vector3.right }, spy.Points); runner.Cancel(skill);
        }

        [Test] public void ParameterPrecedenceLevelAndRenamesAreStable()
        {
            var parameter = new SkillParameter { displayName = "伤害", levels = new[] { 10f, 20f } };
            var defaults = New<SkillParameterSetAsset>(); defaults.parameters = new[] { parameter };
            var skill = Skill(new ApplyEffectAction { effect = Effect(parameter: parameter.Id), targets = new TargetQuery { kind = TargetQueryKind.Self } });
            skill.parameterSet = defaults;
            skill.parameterOverrides = new[] { new SkillParameterOverride { parameterId = parameter.Id, value = 30 } };
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var runner = new SkillRunner(owner, null); runner.Grant(skill, 2);
            runner.TryCast(skill, parameterOverrides: new[] { new SkillParameterOverride { parameterId = parameter.Id, value = 40 } });
            Assert.AreEqual(60, owner.Attributes.GetCurrent(new AttributeId(1)));
            parameter.displayName = "重命名"; skill.name = "变体";
            runner.TryCast(skill); Assert.AreEqual(30, owner.Attributes.GetCurrent(new AttributeId(1)));
            skill.parameterOverrides = Array.Empty<SkillParameterOverride>(); runner.TryCast(skill);
            Assert.AreEqual(10, owner.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void MissingPrefabAndParameterFailBeforeCost()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var action = new SpawnPrefabAction(); var skill = Skill(action);
            Edit(skill, so => so.FindProperty("cost").objectReferenceValue = Effect(-20));
            var runner = new SkillRunner(owner, null);
            Assert.AreEqual(AbilityActivationResult.InvalidConfiguration, runner.TryCast(skill));
            Assert.AreEqual(100, owner.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(action.Id, runner.GetSpec(skill).LastExecutionFailure.SourceId);
            skill.activationActions = new SkillAction[] { new ApplyEffectAction { effect = Effect(parameter: "deleted-id"), targets = new TargetQuery { kind = TargetQueryKind.Self } } };
            Assert.AreEqual(AbilityActivationResult.InvalidConfiguration, runner.TryCast(skill));
            StringAssert.Contains("deleted-id", runner.GetSpec(skill).LastExecutionFailure.Message);
            Assert.AreEqual(100, owner.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void SharedModuleBuildRejectsCyclesAndDepthNine()
        {
            var a = New<SkillActionModuleAsset>(); var b = New<SkillActionModuleAsset>();
            a.actions = new SkillAction[] { new CallModuleAction { module = b } };
            b.actions = new SkillAction[] { new CallModuleAction { module = a } };
            Assert.Throws<SkillActionException>(() => a.Build());
            SkillAction child = new ApplyEffectAction { effect = Effect() };
            for (int i = 0; i < 9; i++) child = new ConditionalAction { whenTrue = new[] { child } };
            Assert.Throws<SkillActionException>(() => new SkillActionBuildContext().Build(new[] { child }));
        }
        [Test] public void ProjectileOutlivesAnimationAndSourceRecycling()
        {
            var owner = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward * 3); var pool = new Pool();
            var parameter = new SkillParameter { displayName = "伤害", defaultValue = 20 };
            var prefab = Go("prefab"); prefab.SetActive(false);
            var skill = Skill(new LaunchProjectileAction { prefab = prefab, speed = new SkillNumber(1), lifetime = new SkillNumber(5),
                onHit = new SkillAction[] { new ApplyEffectAction { effect = Effect(parameter: parameter.Id) } } });
            skill.parameters = new[] { parameter };
            var animation = New<SkillAnimationAsset>(); Edit(animation, so => { so.FindProperty("clipName").stringValue = "Test"; });
            Edit(skill, so => so.FindProperty("animation").objectReferenceValue = animation);
            var runner = new SkillRunner(owner.AbilitySystem, () => new Driver());
            Physics.SyncTransforms(); runner.TryCast(skill, target.AbilitySystem, Services(owner.transform, target.transform, pool));
            owner.AbilitySystem.Tick(.3f); Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(0, owner.AbilitySystem.ActiveTaskCount);
            Assert.AreEqual(1, _scene.Count);
            owner.AbilitySystem.Clear(); parameter.defaultValue = 999;
            owner.transform.position = Vector3.left * 100; Physics.SyncTransforms();
            for (int i = 0; i < 40 && _scene.Count > 0; i++) _scene.Tick(.1f);
            Assert.AreEqual(80, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(1, pool.Returned); Assert.AreEqual(0, _scene.Count);
        }
        [Test] public void TargetQueryDeduplicatesSortsFiltersAndKeepsNestedBuffers()
        {
            var owner = Unit("source", Vector3.zero); var near = Unit("near", Vector3.forward); var far = Unit("far", Vector3.forward * 2);
            near.gameObject.AddComponent<BoxCollider>(); var rejected = Unit("rejected", Vector3.forward * .5f); rejected.AbilitySystem.OwnedTags.AddTag("Test.Other");
            Physics.SyncTransforms(); var skill = Skill(); var runner = new SkillRunner(owner.AbilitySystem, null); runner.TryCast(skill, near.AbilitySystem, Services(owner.transform, near.transform));
            var context = runner.GetExecution(skill); var outer = context.RentBuffer(); var inner = context.RentBuffer();
            try
            {
                var query = new TargetQuery { kind = TargetQueryKind.Sector, blockedTags = new[] { (GameplayTag)"Test.Other" }, maxTargets = 2 }.Build();
                context.Services.Searcher.Query(query, context, outer);
                Assert.AreEqual(2, outer.Count); Assert.AreSame(near.AbilitySystem, outer.Units[0]); Assert.AreSame(far.AbilitySystem, outer.Units[1]);
                context.Services.Searcher.Query(new TargetQuery { kind = TargetQueryKind.Self }.Build(), context, inner);
                Assert.AreSame(near.AbilitySystem, outer.Units[0]); Assert.AreSame(owner.AbilitySystem, inner.Units[0]);
            }
            finally { context.ReturnBuffer(inner); context.ReturnBuffer(outer); }
        }
        [Test] public void ChannelCancellationRemovesOwnAreaEffectAndReturnsVisual()
        {
            var owner = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward); var pool = new Pool();
            var buff = Effect(5); Edit(buff, so => so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Infinite);
            var prefab = Go("area"); prefab.SetActive(false);
            var skill = Skill(new CreateAreaAction { prefab = prefab, enterEffect = buff, ownership = SkillTaskOwnership.Ability, duration = new SkillNumber(10) });
            var runner = new SkillRunner(owner.AbilitySystem, null); Physics.SyncTransforms();
            runner.TryCast(skill, target.AbilitySystem, Services(owner.transform, target.transform, pool)); owner.AbilitySystem.Tick(.1f);
            Assert.AreEqual(105, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1))); Assert.IsTrue(runner.IsActive(skill));
            runner.Cancel(skill); Assert.AreEqual(100, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(1, pool.Returned); Assert.AreEqual(0, owner.AbilitySystem.ActiveTaskCount);
        }
        [Test] public void WaitEventCatchesSameCastMessageAndCancellationUnsubscribes()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var wait = new WaitSkillAction { kind = SkillWaitKind.GameplayEvent, eventTag = "Test.Other", timeout = new SkillNumber(2),
                onSuccess = new SkillAction[] { new ApplyEffectAction { effect = Effect(), targets = new TargetQuery { kind = TargetQueryKind.Self } } } };
            var skill = Skill(wait, new SendGameplayEventAction { tag = "Test.Other" }); var runner = new SkillRunner(owner, null);
            runner.TryCast(skill); owner.Tick(.1f); owner.Tick(.1f);
            Assert.AreEqual(90, owner.Attributes.GetCurrent(new AttributeId(1))); Assert.IsFalse(runner.IsActive(skill));
            skill.activationActions = new SkillAction[] { wait }; runner.TryCast(skill); runner.Cancel(skill);
            owner.Events.Send(new GameplayEvent("Test.Other", owner, new SkillParameterValue(0))); owner.Tick(3);
            Assert.AreEqual(90, owner.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void IndependentSpawnHostClearReturnsObjectsExactlyOnce()
        {
            var owner = Unit("source", Vector3.zero); var prefab = Go("prefab"); prefab.SetActive(false); var pool = new Pool();
            var skill = Skill(new SpawnPrefabAction { prefab = prefab }); var runner = new SkillRunner(owner.AbilitySystem, null);
            runner.TryCast(skill, taskContext: Services(owner.transform, pool: pool));
            runner.TryCast(skill, taskContext: Services(owner.transform, pool: pool));
            Assert.AreEqual(2, _scene.Count); Assert.AreEqual(0, owner.AbilitySystem.ActiveTaskCount);
            _scene.Clear(); _scene.Clear(); Assert.AreEqual(2, pool.Returned);
        }
        [Test] public void CancelDuringSynchronousActionStopsFollowingActions()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var skill = Skill(new SendGameplayEventAction { tag = "Test.Other" },
                new ApplyEffectAction { effect = Effect(), targets = new TargetQuery { kind = TargetQueryKind.Self } });
            var runner = new SkillRunner(owner, null);
            owner.Events.Received += _ => runner.Cancel(skill);
            runner.TryCast(skill);
            Assert.AreEqual(100, owner.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(0, owner.ActiveTaskCount);
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
            new SkillRunner(owner, null).TryCast(skill, taskContext: new TimelineTaskContext { TargetSelection = service });
            Assert.AreEqual(1, service.Disposed); Assert.AreEqual(0, owner.ActiveTaskCount);
        }
        [Test] public void EditingParametersAndDefinitionsOnlyChangesNextCast()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var parameter = new SkillParameter { displayName = "伤害", defaultValue = 10 };
            var effect = Effect(parameter: parameter.Id);
            var wait = new WaitSkillAction { timeout = new SkillNumber(.1f), onSuccess = new SkillAction[] { new ApplyEffectAction { effect = effect, targets = new TargetQuery { kind = TargetQueryKind.Self } } } };
            var skill = Skill(wait); skill.parameters = new[] { parameter };
            var cost = Effect(-1); Edit(skill, so => so.FindProperty("cost").objectReferenceValue = cost);
            var runner = new SkillRunner(owner, null); runner.TryCast(skill);
            parameter.defaultValue = 20; Edit(cost, so => so.FindProperty("modifiers.Array.data[0].magnitude.constant").floatValue = -2);
            cost.InvalidateDefinition(); skill.InvalidateDefinition(); owner.Tick(.2f); owner.Tick(0);
            Assert.AreEqual(89, owner.Attributes.GetCurrent(new AttributeId(1)));
            runner.TryCast(skill); owner.Tick(.2f); owner.Tick(0);
            Assert.AreEqual(67, owner.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void OldDetachedFailureDoesNotCancelNewCast()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward * 2);
            var prefab = Go("projectile"); prefab.SetActive(false);
            var skill = Skill(new LaunchProjectileAction { prefab = prefab, speed = new SkillNumber(1),
                onHit = new SkillAction[] { new SendGameplayEventAction { tag = "Test.Other", targets = new TargetQuery() } } });
            var runner = new SkillRunner(source.AbilitySystem, null);
            Physics.SyncTransforms(); runner.TryCast(skill, target.AbilitySystem, Services(source.transform, target.transform));
            skill.activationActions = new SkillAction[] { new WaitSkillAction { timeout = new SkillNumber(10) } };
            runner.TryCast(skill); target.AbilitySystem.Events.Received += _ => throw new InvalidOperationException("impact failure");
            bool threw = false;
            for (int i = 0; i < 30 && !threw; i++)
                try { _scene.Tick(.1f); } catch (InvalidOperationException) { threw = true; }
            Assert.IsTrue(threw); Assert.IsTrue(runner.IsActive(skill));
            Assert.AreEqual(0, _scene.Count); runner.Cancel(skill);
        }
        [Test] public void ModuleBindingsKeepEachCastAndLevelIndependent()
        {
            var local = new SkillParameter { defaultValue = 1 }; var shared = new SkillParameter { levels = new[] { 10f, 20f } };
            var set = New<SkillParameterSetAsset>(); set.parameters = new[] { local };
            var module = New<SkillActionModuleAsset>(); module.parameters = set;
            module.actions = new SkillAction[] { new ApplyEffectAction { effect = Effect(parameter: local.Id), targets = new TargetQuery { kind = TargetQueryKind.Self } } };
            var skill = Skill(new CallModuleAction { module = module, bindings = new[] { new ModuleParameterBinding { moduleParameterId = local.Id, callerParameterId = shared.Id } } });
            skill.parameters = new[] { shared };
            var first = new AbilitySystemComponent(); first.Attributes.Add(new AttributeId(1), 100);
            var second = new AbilitySystemComponent(); second.Attributes.Add(new AttributeId(1), 100);
            var a = new SkillRunner(first, null); var b = new SkillRunner(second, null); b.Grant(skill, 99);
            a.TryCast(skill); b.TryCast(skill);
            Assert.AreEqual(90, first.Attributes.GetCurrent(new AttributeId(1))); Assert.AreEqual(80, second.Attributes.GetCurrent(new AttributeId(1)));
            module.name = "rename"; shared.displayName = "renamed"; a.TryCast(skill);
            Assert.AreEqual(80, first.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void WarmQueriesAndIdleSceneTicksDoNotAllocate()
        {
            var source = Unit("source", Vector3.zero); Unit("target", Vector3.forward);
            var skill = Skill(new CreateAreaAction { duration = new SkillNumber(10000), period = new SkillNumber(0) });
            var runner = new SkillRunner(source.AbilitySystem, null); Physics.SyncTransforms(); runner.TryCast(skill, taskContext: Services(source.transform));
            var context = runner.GetExecution(skill); var buffer = context.RentBuffer(); var query = new TargetQuery { kind = TargetQueryKind.Circle }.Build();
            for (int i = 0; i < 100; i++) { context.Services.Searcher.Query(query, context, buffer); _scene.Tick(.001f); }
            Assert.IsTrue(AllocationMeter.Works(), "GC allocation recorder must detect a real allocation before trusting zero");
            using var meter = new AllocationMeter();
            for (int i = 0; i < 1000; i++) { context.Services.Searcher.Query(query, context, buffer); _scene.Tick(.001f); }
            long allocated = meter.Stop();
            context.ReturnBuffer(buffer); Assert.AreEqual(0, allocated, "Warm, unchanged occupancy query/Tick path only");
        }
        [Test] public void EventsStopAfterFirstReceiverCancelsCast()
        {
            var source = Unit("source", Vector3.zero); var first = Unit("first", Vector3.forward); var second = Unit("second", Vector3.forward * 2);
            var skill = Skill(new SendGameplayEventAction { tag = "Test.Other", targets = new TargetQuery { kind = TargetQueryKind.Circle } });
            var runner = new SkillRunner(source.AbilitySystem, null); int received = 0;
            first.AbilitySystem.Events.Received += _ => { received++; runner.Cancel(skill); };
            second.AbilitySystem.Events.Received += _ => received++;
            Physics.SyncTransforms(); runner.TryCast(skill, taskContext: Services(source.transform)); Assert.AreEqual(1, received);
        }
        [Test] public void SaturatedEventQueryFailsBeforeSendingAnything()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward);
            for (int i = 0; i < 256; i++) target.gameObject.AddComponent<BoxCollider>();
            int received = 0; target.AbilitySystem.Events.Received += _ => received++;
            var skill = Skill(new SendGameplayEventAction { tag = "Test.Other", targets = new TargetQuery { kind = TargetQueryKind.Circle } });
            Physics.SyncTransforms(); Assert.AreEqual(AbilityActivationResult.ExecutionFailed, new SkillRunner(source.AbilitySystem, null).TryCast(skill, taskContext: Services(source.transform)));
            Assert.AreEqual(0, received);
        }
        [TestCase(false, 0), TestCase(true, 0), TestCase(false, 1), TestCase(true, 1), TestCase(false, 2), TestCase(true, 2)]
        public void SpawnInitializerCancellationIsNormalAndReturnsObjectOnce(bool owned, int kind)
        {
            var source = Unit("source", Vector3.zero); var prefab = Go("prefab"); prefab.SetActive(false); prefab.AddComponent<SkillCancelInitializer>();
            var ownership = owned ? SkillTaskOwnership.Ability : SkillTaskOwnership.Scene;
            SkillAction action = kind == 0 ? new SpawnPrefabAction { prefab = prefab, count = new SkillNumber(3), ownership = ownership }
                : kind == 1 ? new LaunchProjectileAction { prefab = prefab, ownership = ownership }
                : new CreateAreaAction { prefab = prefab, ownership = ownership };
            var pool = new Pool(); var skill = Skill(action); var runner = new SkillRunner(source.AbilitySystem, null);
            Assert.DoesNotThrow(() => runner.TryCast(skill, taskContext: Services(source.transform, pool: pool)));
            Assert.AreEqual(1, pool.Created); Assert.AreEqual(1, pool.Returned); Assert.AreEqual(0, _scene.Count);
            Assert.AreNotEqual(AbilityActivationResult.ExecutionFailed, runner.GetSpec(skill).LastExecutionFailure.Result);
        }
        [Test] public void DetachedAndCancellationBranchesRejectOwnedWorkBeforeCasting()
        {
            var prefab = Go("prefab");
            foreach (var forbidden in new SkillAction[] { new WaitSkillAction(), new SpawnPrefabAction { prefab = prefab, ownership = SkillTaskOwnership.Ability },
                new CreateAreaAction { ownership = SkillTaskOwnership.Ability } })
            {
                var module = New<SkillActionModuleAsset>(); module.actions = new[] { forbidden };
                var call = new CallModuleAction { module = module };
                Assert.Throws<SkillActionException>(() => Skill(new WaitSkillAction { onCancel = new SkillAction[] { call } }).BuildActions());
                Assert.Throws<SkillActionException>(() => Skill(new LaunchProjectileAction { prefab = prefab, onHit = new SkillAction[] { call } }).BuildActions());
            }
        }
        [Test] public void MissingTargetPointFailsBeforeCostButExplicitOriginIsValid()
        {
            var source = Unit("source", Vector3.one); var prefab = Go("prefab"); prefab.SetActive(false);
            var service = new TimelineTaskContext { Origin = source.transform, SceneTasks = _scene, Objects = new Pool() };
            foreach (SkillAction action in new SkillAction[] { new LaunchProjectileAction { prefab = prefab }, new SpawnPrefabAction { prefab = prefab, position = SkillSpawnPosition.TargetPoint } })
            {
                var skill = Skill(action); Edit(skill, so => so.FindProperty("cost").objectReferenceValue = Effect(-10)); var runner = new SkillRunner(source.AbilitySystem, null);
                Assert.AreEqual(AbilityActivationResult.InvalidConfiguration, runner.TryCast(skill, taskContext: service)); Assert.AreEqual(100, source.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            }
            service.TargetPosition = Vector3.zero;
            Assert.DoesNotThrow(() => new SkillRunner(source.AbilitySystem, null).TryCast(Skill(new LaunchProjectileAction { prefab = prefab }), taskContext: service));
            var target = Unit("target", Vector3.forward);
            service = new TimelineTaskContext { Origin = source.transform, Target = target.transform, SceneTasks = _scene, Objects = new Pool() };
            Assert.DoesNotThrow(() => new SkillRunner(source.AbilitySystem, null).TryCast(Skill(new SpawnPrefabAction { prefab = prefab, position = SkillSpawnPosition.TargetPoint }), taskContext: service));
        }
        [Test] public void AreaCapKeepsAdmittedOccupantsUntilTheyActuallyLeave()
        {
            var source = Unit("source", Vector3.zero); var first = Unit("first", Vector3.forward); var second = Unit("second", Vector3.forward * 2);
            var effect = Effect(0); Edit(effect, so => { so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration; so.FindProperty("duration").floatValue = 100; });
            var skill = Skill(new CreateAreaAction { duration = new SkillNumber(20), period = new SkillNumber(0), enterEffect = effect,
                targets = new TargetQuery { kind = TargetQueryKind.Circle, maxTargets = 1, radius = new SkillNumber(3) } });
            Physics.SyncTransforms(); new SkillRunner(source.AbilitySystem, null).TryCast(skill, taskContext: Services(source.transform)); _scene.Tick(.1f);
            Assert.AreEqual(1, first.AbilitySystem.Effects.Count); Assert.AreEqual(0, second.AbilitySystem.Effects.Count);
            second.transform.position = Vector3.forward * .5f; Physics.SyncTransforms(); _scene.Tick(.1f);
            Assert.AreEqual(1, first.AbilitySystem.Effects.Count); Assert.AreEqual(0, second.AbilitySystem.Effects.Count);
            first.transform.position = Vector3.forward * 10; Physics.SyncTransforms(); _scene.Tick(.1f);
            Assert.AreEqual(0, first.AbilitySystem.Effects.Count); Assert.AreEqual(1, second.AbilitySystem.Effects.Count);
        }
        [Test] public void GrantsUpdateAndSetsRevokeOnlyTheirOwnContribution()
        {
            var owner = new AbilitySystemComponent(); var parameter = new SkillParameter { defaultValue = 1 }; var skill = Skill(); skill.parameters = new[] { parameter };
            var runner = new SkillRunner(owner, null); runner.Grant(skill, 2); runner.Grant(skill, 3);
            runner.TryCast(skill); Assert.AreEqual(3, runner.GetExecution(skill).Values.Level);
            var first = New<AbilitySetAsset>(); first.abilities = new[] { new AbilitySetAsset.Entry { skill = skill, level = 4 } };
            var second = New<AbilitySetAsset>(); second.abilities = new[] { new AbilitySetAsset.Entry { skill = skill, level = 5 } };
            first.GrantTo(runner); second.GrantTo(runner); first.RevokeFrom(runner); Assert.AreEqual(5, runner.GetSpec(skill).Level);
            second.abilities = Array.Empty<AbilitySetAsset.Entry>(); second.RevokeFrom(runner); Assert.AreEqual(3, runner.GetSpec(skill).Level);
            var levels = new[] { 7f }; runner.Grant(skill, 2, new[] { new SkillParameterOverride { parameterId = parameter.Id, levels = levels } }); levels[0] = 999;
            runner.TryCast(skill); Assert.AreEqual(7, runner.GetExecution(skill).Values.Parameter(parameter.Id));
        }
        [Test] public void ImplicitGrantIsSupersededAndPublicRevokePreservesSetContributions()
        {
            var owner = new AbilitySystemComponent(); var runner = new SkillRunner(owner, null); var skill = Skill();
            runner.TryCast(skill); var set = New<AbilitySetAsset>(); set.abilities = new[] { new AbilitySetAsset.Entry { skill = skill, level = 3 } };
            set.GrantTo(runner); Assert.IsFalse(runner.Revoke(skill), "No explicit direct contribution exists.");
            set.RevokeFrom(runner); Assert.IsNull(runner.GetSpec(skill), "Implicit first-use ownership must not survive a real grant.");
            runner.Grant(skill, 2); set.GrantTo(runner); Assert.IsTrue(runner.Revoke(skill)); Assert.AreEqual(3, runner.GetSpec(skill).Level);
            set.RevokeFrom(runner); Assert.IsNull(runner.GetSpec(skill));
            set.GrantTo(runner); Assert.IsTrue(runner.RevokeAll(skill)); Assert.IsNull(runner.GetSpec(skill));
        }
        [Test] public void ClearRegrantsOriginalSetLevelAndOverridesAndStillRevokesCorrectly()
        {
            var owner = new AbilitySystemComponent(); var runner = new SkillRunner(owner, null); var parameter = new SkillParameter { defaultValue = 1 };
            var skill = Skill(); skill.parameters = new[] { parameter };
            var set = New<AbilitySetAsset>(); set.abilities = new[] { new AbilitySetAsset.Entry { skill = skill, level = 3,
                parameters = new[] { new SkillParameterOverride { parameterId = parameter.Id, value = 7 } } } };
            set.GrantTo(runner); var previous = runner.GetSpec(skill); owner.Clear();
            Assert.IsNull(runner.GetSpec(skill)); Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(0, owner.GrantedAbilityCount);
            runner.TryCast(skill);
            Assert.AreNotSame(previous, runner.GetSpec(skill)); Assert.AreEqual(3, runner.GetExecution(skill).Values.Level);
            Assert.AreEqual(7, runner.GetExecution(skill).Values.Parameter(parameter.Id)); set.RevokeFrom(runner);
            Assert.IsNull(runner.GetSpec(skill)); Assert.AreEqual(0, owner.GrantedAbilityCount);
            set.GrantTo(runner); owner.Clear(); set.RevokeFrom(runner); Assert.AreEqual(0, owner.GrantedAbilityCount); Assert.IsNull(runner.GetSpec(skill));
        }
        [Test] public void TargetRebindingWithoutTransformPreservesAimAndHostCanClearIt()
        {
            var owner = new AbilitySystemComponent(); var target = new AbilitySystemComponent(); var spec = owner.GrantAbility(new GameplayAbilityDefinition());
            owner.TryActivateAbility(spec); var services = new TimelineTaskContext { TargetPosition = Vector3.right * 7 };
            var context = new SkillExecutionContext(null, spec, null, spec.EvaluationContext, services);
            Assert.AreEqual(services.TargetPosition, context.WithTarget(target).Services.RequireTargetPosition());
            services.ClearTargetPosition(); Assert.IsFalse(services.HasTargetPosition); Assert.Throws<InvalidOperationException>(() => services.RequireTargetPosition());
            Assert.IsTrue(context.Services.HasTargetPosition, "Clearing reusable host services must not change an existing cast snapshot.");
            context.Services.ClearTargetPosition(); Assert.IsFalse(context.WithTarget(target).Services.HasTargetPosition); owner.Clear();
        }
        [Test] public void InvalidFirstGrantDoesNotLeaveAnEntryWithoutGrantProvenance()
        {
            var skill = Skill(); var cost = Effect(0); var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            Edit(cost, so => { so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration; so.FindProperty("duration").floatValue = 1; });
            Edit(skill, so => so.FindProperty("cost").objectReferenceValue = cost);
            var runner = new SkillRunner(owner, null); Assert.AreEqual(AbilityActivationResult.InvalidConfiguration, runner.TryCast(skill));
            Assert.IsInstanceOf<ArgumentException>(runner.LastCastCheck.Error);
            Assert.IsNull(runner.GetSpec(skill)); Assert.AreEqual(0, owner.GrantedAbilityCount);
            Edit(cost, so => so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Instant); cost.InvalidateDefinition(); skill.InvalidateDefinition();
            Assert.AreEqual(AbilityActivationResult.Activated, runner.TryCast(skill)); Assert.IsTrue(runner.Revoke(skill)); Assert.IsNull(runner.GetSpec(skill));
        }
        [Test] public void ThrowingAreaExitDoesNotFailInterruptingCastOrCancelDetachedObjects()
        {
            var source = Unit("source", default); var target = Unit("target", Vector3.forward); var prefab = Go("prefab");
            var skill = Skill(new SpawnPrefabAction { prefab = prefab }, new CreateAreaAction { ownership = SkillTaskOwnership.Ability,
                duration = new SkillNumber(20), targets = new TargetQuery { kind = TargetQueryKind.FixedTarget }, exitEffect = Effect(-1) });
            Edit(skill, so => { so.FindProperty("abilityTags").arraySize = 1; so.FindProperty("abilityTags.Array.data[0].m_Name").stringValue = "Test.Other"; });
            var next = Skill(); Edit(next, so => { so.FindProperty("cancelAbilitiesWithTags").arraySize = 1; so.FindProperty("cancelAbilitiesWithTags.Array.data[0].m_Name").stringValue = "Test.Other"; });
            var runner = new SkillRunner(source.AbilitySystem, null); runner.TryCast(skill, target.AbilitySystem, Services(source.transform, target.transform));
            source.AbilitySystem.Tick(.01f); target.AbilitySystem.Attributes.Get(new AttributeId(1)).OnCurrentValueChanged += (_, _, _) => throw new InvalidOperationException("area exit callback");
            Assert.DoesNotThrow(() => Assert.AreEqual(AbilityActivationResult.Activated, runner.TryCast(next)));
            Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(1, _scene.Count);
            Assert.IsNull(runner.GetExecution(skill).Error); StringAssert.Contains("area exit callback", runner.GetSpec(skill).LastCleanupError);
            Assert.AreEqual(0, source.AbilitySystem.ActiveTaskCount);
        }
        [Test] public void WarmPeriodicModuleBindingsDoNotAllocate()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward);
            var module = New<SkillActionModuleAsset>(); module.actions = new SkillAction[] { new SendGameplayEventAction { tag = "Test.Other" } };
            var skill = Skill(new CreateAreaAction { duration = new SkillNumber(10000), period = new SkillNumber(.01f), onPeriod = new SkillAction[] { new CallModuleAction { module = module } } });
            Physics.SyncTransforms(); new SkillRunner(source.AbilitySystem, null).TryCast(skill, taskContext: Services(source.transform));
            for (int i = 0; i < 100; i++) _scene.Tick(.02f);
            Assert.IsTrue(AllocationMeter.Works()); using var meter = new AllocationMeter();
            for (int i = 0; i < 1000; i++) _scene.Tick(.02f);
            long allocated = meter.Stop(); Assert.AreEqual(0, allocated);
        }
        [Test] public void LegacyAreaSpawnerRemainsAbilityOwnedAndUsesCastContext()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward);
            var parameter = new SkillParameter { defaultValue = 7 }; var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(100) }); skill.parameters = new[] { parameter };
            var effect = Effect(parameter: parameter.Id);
            Edit(effect, so => { so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration; so.FindProperty("duration").floatValue = 10; });
            var runner = new SkillRunner(source.AbilitySystem, null); runner.Grant(skill, 3); Physics.SyncTransforms(); runner.TryCast(skill, taskContext: Services(source.transform));
            var legacy = new AreaEffectSpawner { enterEffect = effect, duration = 20, maxOccupants = 1 }; var execution = runner.GetExecution(skill);
            var action = legacy.Build(); action.Validate(execution); execution.ExecuteAction(action); source.AbilitySystem.Tick(.1f);
            Assert.AreEqual(93, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1))); Assert.AreEqual(0, _scene.Count);
            Assert.AreSame(source.AbilitySystem.SourceIdentity, target.AbilitySystem.Effects[0].Source);
            runner.Cancel(skill); Assert.AreEqual(0, target.AbilitySystem.Effects.Count);
        }
        [Test] public void LegacyProjectileSpawnerStillUsesHostVisualPoolAndCancelsWithAbility()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward * 2); var prefab = Go("prefab"); prefab.SetActive(false);
            int rented = 0, returned = 0;
            var services = new TimelineTaskContext { Origin = source.transform, Target = target.transform, SceneTasks = _scene,
                RentVisual = p => { rented++; var instance = Object.Instantiate(p); instance.SetActive(true); return instance.transform; },
                ReturnVisual = visual => { returned++; Object.DestroyImmediate(visual.gameObject); } };
            var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(100) }); var runner = new SkillRunner(source.AbilitySystem, null);
            runner.TryCast(skill, target.AbilitySystem, services);
            var spawner = new ProjectileSpawner { visualPrefab = prefab, impactEffect = Effect() };
            spawner.Spawn(runner.GetSpec(skill), runner.GetExecution(skill).Services);
            Assert.AreEqual(1, rented); Assert.AreEqual(0, _scene.Count);
            runner.Cancel(skill); Assert.AreEqual(1, returned);
        }
        private sealed class SourceRequiredFilter : ITargetFilter
        { public bool Allows(AbilitySystemComponent source, AbilitySystemComponent target) => source.Attributes.Count > 0; }
        private sealed class ThrowingSelection : ISkillTargetSelection
        {
            public IDisposable Request(SkillExecutionContext context, Action<AbilitySystemComponent, Transform> selected)
                => throw new NullReferenceException("picker startup");
        }
        [Test] public void ModuleTaskBeginFailureIsReportedOnceWithTheFullCauseAndOuterCall()
        {
            var owner = new AbilitySystemComponent(); var module = New<SkillActionModuleAsset>(); module.name = "Picker";
            var wait = new WaitSkillAction { kind = SkillWaitKind.Target }; module.actions = new SkillAction[] { wait };
            var call = new CallModuleAction { module = module }; var skill = Skill(call); var runner = new SkillRunner(owner, null);
            Assert.AreEqual(AbilityActivationResult.ExecutionFailed, runner.TryCast(skill, taskContext: new TimelineTaskContext { TargetSelection = new ThrowingSelection() }));
            Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(0, owner.ActiveTaskCount); Assert.AreEqual(1, owner.DiagnosticCount);
            var diagnostic = owner.GetDiagnostic(0); Assert.AreEqual(call.Id, diagnostic.SourceId);
            StringAssert.Contains(wait.Id, diagnostic.Message); Assert.IsInstanceOf<NullReferenceException>(diagnostic.Error.GetBaseException());
            Assert.AreSame(diagnostic.Error, runner.LastCastCheck.Error);
        }
        [TestCase(0, false), TestCase(1, false), TestCase(2, false)]
        [TestCase(0, true), TestCase(1, true), TestCase(2, true)]
        public void DirectTaskHitUsesCastSourcePolicy(int kind, bool frozen)
        {
            var source = Unit("source", Vector3.right * 30); var target = Unit("target", source.transform.position + Vector3.forward);
            var owner = source.AbilitySystem; var health = new AttributeId(1); var attack = new AttributeId(2);
            var value = owner.Attributes.Add(attack, 5); var ability = owner.GrantAbility(new GameplayAbilityDefinition());
            owner.TryActivateAbility(ability, captureSource: frozen);
            var damage = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
                new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.FromAttribute(attack, -1, MagnitudeCapture.Source)) });
            value.BaseValue = 11;
            GameplayAbilityTask task = kind switch
            {
                0 => new AnimationTask(new SkillAnimationDefinition("a", 1, markers: new[] { new AnimationMarker(.1f, effect: damage) }), new Driver(), target.AbilitySystem, 1),
                1 => new ProjectileTask(new ProjectileConfig { Visual = Go("shot", source.transform.position).transform, Speed = 20,
                    Aim = ProjectileAim.Position, TargetPosition = target.transform.position, CollisionRadius = .25f, CollisionMask = ~0, ImpactEffect = damage }, new TargetSearcher(), owner.Attributes, owner),
                _ => new AreaEffectTask(new AreaEffectConfig { Position = target.transform.position, Radius = .25f, CollisionMask = ~0, EnterEffect = damage }, new TargetSearcher(), owner.Attributes, owner)
            };
            owner.StartTask(ability, task); value.BaseValue = 17; Physics.SyncTransforms(); owner.Tick(.2f);
            Assert.AreEqual(frozen ? 95 : 83, target.AbilitySystem.Attributes.GetCurrent(health), "5 = cast time; 11 = task start (never correct); 17 = hit time.");
            owner.Clear();
        }
        private sealed class FailingExternalTask : GameplayAbilityTask
        {
            public override string DiagnosticId => "external-task";
            protected override void OnTick(float dt) => throw new ArgumentException("external task failed");
        }
        private sealed class FaultAction : SkillAction
        {
            public override SkillActionDefinition Build(SkillActionBuildContext builder)
                => Define("delayed fault", null, _ => throw new InvalidOperationException("delayed module failure"));
        }
        [TestCase(false), TestCase(true)] public void ModuleIdentitySurvivesDetachedAndCleanupCallbacks(bool cleanup)
        {
            var source = Unit("source", default); var target = Unit("target", Vector3.forward);
            var fault = new FaultAction(); var module = New<SkillActionModuleAsset>(); module.name = "DelayedModule";
            module.actions = cleanup
                ? new SkillAction[] { new WaitSkillAction { onCancel = new SkillAction[] { fault } } }
                : new SkillAction[] { new CreateAreaAction { period = new SkillNumber(.1f), onPeriod = new SkillAction[] { fault } } };
            var call = new CallModuleAction { module = module }; var skill = Skill(call);
            var runner = new SkillRunner(source.AbilitySystem, null); Physics.SyncTransforms(); runner.TryCast(skill, taskContext: Services(source.transform));
            if (cleanup) Assert.DoesNotThrow(() => runner.Cancel(skill));
            else { Assert.IsFalse(runner.IsActive(skill)); Assert.Throws<InvalidOperationException>(() => _scene.Tick(.2f)); }
            var diagnostic = source.AbilitySystem.GetDiagnostic(0);
            Assert.AreEqual(call.Id, diagnostic.SourceId); Assert.AreEqual(cleanup, diagnostic.Cleanup);
            StringAssert.Contains(fault.Id, diagnostic.Message); StringAssert.Contains(module.name, diagnostic.Message);
            Assert.AreEqual(0, _scene.Count); source.AbilitySystem.Clear(); Assert.AreEqual(1, source.AbilitySystem.DiagnosticCount);
        }
        [Test] public void ExternalTaskFailureEndsAsFailureAndDiagnosticsSurviveRecycle()
        {
            var owner = new AbilitySystemComponent(); var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(10) });
            var runner = new SkillRunner(owner, null); runner.TryCast(skill); owner.StartTask(runner.GetSpec(skill), new FailingExternalTask());
            Assert.Throws<ArgumentException>(() => owner.Tick(.1f));
            Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(0, owner.ActiveTaskCount);
            Assert.AreEqual("执行失败", runner.GetExecution(skill).Status);
            var snapshot = RuntimeSnapshot.Capture(owner, null);
            Assert.AreEqual(AbilityActivationResult.ExecutionFailed, snapshot.Abilities[0].Failure.Result);
            Assert.AreEqual("external-task", snapshot.Abilities[0].Failure.SourceId);
            owner.Clear(); snapshot = RuntimeSnapshot.Capture(owner, null);
            Assert.AreEqual(0, snapshot.Abilities.Count); Assert.AreEqual(1, snapshot.Diagnostics.Count);
            Assert.AreSame(skill, DefinitionSource.Of(snapshot.Diagnostics[0].Definition));
            var window = ScriptableObject.CreateInstance<RuntimeDebugWindow>();
            try
            {
                window.Show(); RuntimeDebugSession.Publish(snapshot);
                var rows = window.rootVisualElement.Q<UnityEngine.UIElements.VisualElement>("debug-diagnostics");
                Assert.IsNotNull(rows);
                Assert.IsTrue(rows.Query<UnityEngine.UIElements.Label>().ToList().Exists(label => label.text.Contains("external task failed")));
                owner.ClearDiagnostics(); RuntimeDebugSession.Publish(RuntimeSnapshot.Capture(owner, null));
                Assert.AreEqual(0, rows.Query<VisualElement>(className: "debug-row").ToList().Count);
                Assert.AreEqual("暂无", rows.Q<Label>("empty").text);
            }
            finally { window.Close(); RuntimeDebugSession.Clear(); }
        }
        [Test] public void RecycledSourceDoesNotReachNonSnapshotFilterAsNull()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward); var services = Services(source.transform, target.transform);
            services.TargetFilter = new SourceRequiredFilter();
            var skill = Skill(new CreateAreaAction { period = new SkillNumber(.01f), onPeriod = new SkillAction[] { new ApplyEffectAction { effect = Effect() } } });
            Physics.SyncTransforms(); new SkillRunner(source.AbilitySystem, null).TryCast(skill, taskContext: services); source.AbilitySystem.Clear();
            Assert.DoesNotThrow(() => _scene.Tick(.1f)); Assert.AreEqual(100, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }
    }
}
