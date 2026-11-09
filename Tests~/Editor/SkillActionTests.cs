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

    // A project-owned action: no reference to this type in the GAS runtime, graph or preview.
    [Serializable, System.ComponentModel.DisplayName("延迟落点")]
    public sealed class TravelBranchAction : SkillAction
    {
        [FlowField("飞行时长")] public SkillNumber travel = new(.4f);
        public SkillTaskOwnership lifetime;
        [SerializeReference, FlowBranch("抵达")] public SkillAction[] afterTravel;
        public override bool IsDetachedBranch(string field) => field == nameof(afterTravel) && lifetime == SkillTaskOwnership.Scene;
        public override SkillActionDefinition Build(SkillActionBuildContext builder)
        {
            if (lifetime == SkillTaskOwnership.Ability) builder.RequireAbilityTask(Id);
            var next = BuildBranch(builder,nameof(afterTravel),afterTravel);
            var owner=lifetime; var duration=travel; var id=Id;
            return Define("延迟落点", c => c.Start(new Flight(c,id,duration.Resolve(c.Values),next),owner));
        }
        private sealed class Flight : SkillActionTask
        {
            private float _remaining;
            private readonly SkillActionDefinition[] _next;
            public Flight(SkillExecutionContext c,string id,float duration,SkillActionDefinition[] next) : base(c,id)
            { _remaining=duration; _next=next; }
            public override string State => "飞行中";
            protected override void Tick(float dt) { if ((_remaining-=dt)<=0) { Complete(); Execution.Execute(_next); } }
        }
        [InitializeOnLoadMethod] private static void RegisterPreview() =>
            SkillActionPreview.Register<TravelBranchAction>((a,f)=>f.Visit(a.afterTravel,a.travel.Resolve(f.Values)));
    }
    internal sealed class TravelFixtureAsset : ScriptableObject
    { [SerializeReference] public SkillAction[] stages; }
    public sealed class SkillActionTests
    {
        private readonly List<Object> _objects = new();
        private readonly GameplayTaskHost _scene = new();
        private sealed class Driver : IAnimationDriver
        { public float Play(string name, float duration, bool loop) => .2f; public void Stop() { } public void Advance(float dt) { } }
        private sealed class AuthoredClock : IAnimationDriver
        { public float Play(string name, float duration, bool loop) => duration; public void Stop() { } public void Advance(float dt) { } }
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
        private static SkillRunner Granted(AbilitySystemComponent owner, Func<IAnimationDriver> driver, GameplayAbilityAsset skill)
        { var runner = new SkillRunner(owner); if (driver != null) new SkillTimelinePlayer(runner, driver); runner.Grant(skill); return runner; }
        private GameplayAbilityAsset Skill(params SkillAction[] actions)
        { var skill = New<GameplayAbilityAsset>(); skill.activationActions = actions; return skill; }
        private TimelineTaskContext Services(Transform origin, Transform target = null, Pool pool = null)
            => new TimelineTaskContext { Origin = origin, Target = target, TargetPosition = target != null ? target.position : Vector3.forward * 3,
                Objects = pool ?? new Pool(), Searcher = new TargetSearcher(), SceneTasks = _scene };

        [Test] public void TurnBasedCastHitsChosenLogicalUnitWithoutSceneOrTimeline()
        {
            var owner = new AbilitySystemComponent(); var chosen = new AbilitySystemComponent();
            chosen.Attributes.Add(new AttributeId(1), 100);
            var skill = Skill(new ApplyEffectAction { effect = Effect(), targets = new TargetQuery {
                kind = TargetQueryKind.FixedTarget, radius = new SkillNumber { parameterId = "unused geometry" }, origin = SkillSpawnPosition.HitPoint } });
            var runner = new SkillRunner(owner); runner.Grant(skill);
            runner.TryCast(skill); Assert.AreEqual(100, chosen.Attributes.GetCurrent(new AttributeId(1)));
            runner.TryCast(skill, new SkillCastTarget(chosen)); Assert.AreEqual(90, chosen.Attributes.GetCurrent(new AttributeId(1)));
        }

        [Test] public void SwordWindowUsesMovingWeaponInsteadOfAimAndStopsOnCancel()
        {
            var caster = Unit("caster", new Vector3(200, 0, 0)); var origin = caster.transform;
            var selected = Unit("selected, outside blade", origin.position + 2 * Vector3.forward);
            var struck = Unit("struck", origin.position + 3 * Vector3.left);
            struck.gameObject.AddComponent<BoxCollider>().size = Vector3.one * .1f;
            var sword = Go("blade"); sword.transform.SetParent(origin, false);
            sword.transform.localRotation = Quaternion.Euler(0, -90, 0);
            sword.transform.localScale = new Vector3(1, 1, 2);
            var blade = sword.AddComponent<BoxCollider>(); blade.center = Vector3.forward; blade.size = new Vector3(.1f,.1f,1.4f);
            origin.gameObject.AddComponent<SkillHitboxes>().slots = new[] { new SkillHitboxes.Slot { key = "Sword", collider = blade } };
            var skill = Skill(); var timeline = New<SkillAnimationAsset>();
            SkillTimelineAuthoring.Edit(timeline, t => t.Clip("swing", 2));
            timeline.hitWindows = new[] { new SkillHitWindow { start = .65f, end = 1.5f, effect = Effect(),
                targets = new TargetQuery { kind = TargetQueryKind.Hitbox, sourceKey = "Sword" } } };
            SkillAssetFactory.SetMainAnimation(skill, timeline);
            var owner = caster.AbilitySystem; var runner = new SkillRunner(owner, new SkillUnitServices { Origin = origin });
            using var player = new SkillTimelinePlayer(runner, () => new AuthoredClock()); runner.Grant(skill);
            float HP(SkillActionTestUnit u) => u.AbilitySystem.Attributes.GetCurrent(new AttributeId(1));
            Physics.SyncTransforms(); runner.TryCast(skill, new SkillCastTarget(selected.AbilitySystem)); owner.Tick(.64f);
            Assert.AreEqual(100, HP(struck));
            owner.Tick(.02f); owner.Tick(.1f); Assert.AreEqual(90, HP(struck)); Assert.AreEqual(100, HP(selected));
            sword.transform.localRotation = Quaternion.identity; Physics.SyncTransforms(); owner.Tick(.1f);
            Assert.AreEqual(90, HP(selected));
            runner.Cancel(skill); var fresh = Unit("after interrupt", selected.transform.position);
            Physics.SyncTransforms(); owner.Tick(.2f); Assert.AreEqual(100, HP(fresh));
        }

        [TestCase(false)] [TestCase(true)]
        public void RoundWeaponVolumesAgreeWithUnityColliderAndPreview(bool capsule)
        {
            var caster = Unit("caster", new Vector3(250,0,0)); var weapon = Go("weapon");
            weapon.transform.SetParent(caster.transform, false);
            weapon.transform.localScale = new Vector3(2, 1, .5f); weapon.transform.rotation = Quaternion.Euler(0,0,40);
            Collider collider;
            if(capsule) { var c = weapon.AddComponent<CapsuleCollider>(); c.direction=0; c.radius=.25f; c.height=2; collider=c; }
            else { var s=weapon.AddComponent<SphereCollider>(); s.radius=.5f; collider=s; }
            caster.gameObject.AddComponent<SkillHitboxes>().slots = new[] { new SkillHitboxes.Slot { key="weapon", collider=collider } };
            var inside=Unit("inside",weapon.transform.position+weapon.transform.right*(capsule?1.8f:.9f));
            var outside=Unit("outside",weapon.transform.position+weapon.transform.up*(capsule?.6f:1.4f));
            Physics.SyncTransforms(); Assert.IsTrue(SkillColliderVolume.TryRead(collider, out var volume));
            foreach(var unit in new[]{inside,outside}) {
                Vector3 p=unit.transform.position;
                Assert.AreEqual((collider.ClosestPoint(p)-p).sqrMagnitude <= .2f*.2f, volume.TouchesSphere(p,.2f));
            }
            var skill=Skill(new ApplyEffectAction { effect=Effect(), targets=new TargetQuery { kind=TargetQueryKind.Hitbox, sourceKey="weapon" } });
            var runner = new SkillRunner(caster.AbilitySystem,new SkillUnitServices { Origin=caster.transform }); runner.Grant(skill); runner.TryCast(skill);
            Assert.AreEqual(90,inside.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(100,outside.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }

        private sealed class BoardSelection : ISkillTargetSource
        {
            internal AbilitySystemComponent[] InOrder;
            public void Collect(string key, SkillExecutionContext cast, SkillTargetResults list)
            {
                Assert.AreEqual("chosen-row", key);
                foreach (var unit in InOrder) list.Add(unit);
            }
        }
        [Test] public void ProjectSelectionRespectsPriorityFilterAndLimitWithoutInventingWorldPoints()
        {
            var owner = new AbilitySystemComponent(); var a = new AbilitySystemComponent(); var b = new AbilitySystemComponent();
            foreach (var u in new[] { owner, a, b }) u.Attributes.Add(new AttributeId(1), 100);
            var input = new BoardSelection { InOrder = new[] { owner, a, a, b } };
            var services = new SkillUnitServices(); services.Set<ISkillTargetSource>(input);
            services.TargetRelations = (_, unit, _) => unit != owner;
            var run = new SkillRunner(owner, services);
            var skill = Skill(new SelectTargetsAction { targets = new TargetQuery { kind = TargetQueryKind.Project, sourceKey = "chosen-row", maxTargets = 1, relation = TargetRelation.Enemy },
                actions = new SkillAction[] { new ApplyEffectAction { effect = Effect() } } });
            run.Grant(skill); run.TryCast(skill); Assert.AreEqual(90, a.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(100, b.Attributes.GetCurrent(new AttributeId(1))); Assert.AreEqual(100, owner.Attributes.GetCurrent(new AttributeId(1)));
            input.InOrder = new[] { b }; run.TryCast(skill); Assert.AreEqual(90, b.Attributes.GetCurrent(new AttributeId(1)));
        }

        [Test] public void ForwardHitWindowIncludesLateEntriesOnceAndClosesAtItsRightEdge()
        {
            var source = Unit("caster", new Vector3(180, 0, 0));
            var center = source.transform.position + Vector3.up;
            var front = Unit("front", center + Vector3.right);
            var back = Unit("back", center - Vector3.right);
            var late = Unit("late", center + 5 * Vector3.forward);
            var end = Unit("end", late.transform.position);
            var skill = Skill();
            var timeline = New<SkillAnimationAsset>();
            SkillTimelineAuthoring.Edit(timeline, t => t.Clip("swing", 2));
            timeline.hitWindows = new[] { new SkillHitWindow { start = .65f, end = 1.5f, effect = Effect(),
                offset = Vector3.up, yaw = 90, targets = new TargetQuery { kind = TargetQueryKind.Line, radius = new SkillNumber(2) } } };
            SkillAssetFactory.SetMainAnimation(skill, timeline);
            var owner = source.AbilitySystem;
            var runner = new SkillRunner(owner, new SkillUnitServices { Origin = source.transform });
            using var player = new SkillTimelinePlayer(runner, () => new AuthoredClock());
            runner.Grant(skill);
            float HP(SkillActionTestUnit u) => u.AbilitySystem.Attributes.GetCurrent(new AttributeId(1));
            Physics.SyncTransforms(); runner.TryCast(skill); owner.Tick(.64f);
            Assert.AreEqual(100, HP(front));
            owner.Tick(.02f); Assert.AreEqual(90, HP(front)); Assert.AreEqual(100, HP(back));
            front.transform.position = center - Vector3.right;
            late.transform.position = center + Vector3.right;
            Physics.SyncTransforms(); owner.Tick(.34f);
            Assert.AreEqual(90, HP(late));
            front.transform.position = center + Vector3.right; Physics.SyncTransforms(); owner.Tick(.25f);
            Assert.AreEqual(90, HP(front), "Re-entry is still the same swing.");
            end.transform.position = center + Vector3.right; Physics.SyncTransforms(); owner.Tick(.25f);
            Assert.AreEqual(100, HP(end), "The closing sample at 1.50 must not deal damage.");
            runner.Cancel(skill);
            runner.TryCast(skill); owner.Tick(.75f); Assert.AreEqual(80, HP(front));
            runner.Cancel(skill); back.transform.position = center + Vector3.right; Physics.SyncTransforms();
            owner.Tick(1); Assert.AreEqual(100, HP(back));
        }

        [Test] public void BoundServicesUseAssetRelationAndDoNotLeakAimBetweenCasts()
        {
            Assert.That(SkillActionFields.For(typeof(ApplyEffectAction)).Inline, Does.Contain(("targets.relation", "目标关系")));
            Assert.That(SkillActionFields.For(typeof(LaunchProjectileAction)).Inline, Does.Contain(("collision.relation", "目标关系")));
            var source = Unit("caster", Vector3.zero); var ally = Unit("ally", Vector3.right);
            var enemy = Unit("enemy", Vector3.forward); Physics.SyncTransforms();
            var services = new SkillUnitServices { Origin = source.transform, TargetRelations = (from, to, relation) =>
                from == source.AbilitySystem.SourceIdentity && (relation == TargetRelation.Enemy) == (to == enemy.AbilitySystem) };
            var runner = new SkillRunner(source.AbilitySystem, services);
            GameplayAbilityAsset Area(TargetRelation side, float amount) => Skill(new ApplyEffectAction {
                effect = Effect(amount), targets = new TargetQuery { kind = TargetQueryKind.Circle, relation = side, excludeSelf = false } });
            var attack = Area(TargetRelation.Enemy, -20); var heal = Area(TargetRelation.Ally, 5);
            runner.Grant(attack); runner.Grant(heal);
            runner.TryCast(attack, new SkillCastTarget(enemy.AbilitySystem, enemy.transform, Vector3.zero));
            Assert.AreEqual(Vector3.zero, runner.GetExecution(attack).Services.RequireTargetPosition());
            runner.TryCast(heal, SkillCastTarget.Ground(Vector3.right));
            float HP(SkillActionTestUnit u) => u.AbilitySystem.Attributes.GetCurrent(new AttributeId(1));
            Assert.AreEqual(80, HP(enemy)); Assert.AreEqual(105, HP(ally)); Assert.AreEqual(105, HP(source));
            runner.TryCast(attack, new SkillCastTarget(enemy.AbilitySystem, enemy.transform));
            Assert.AreEqual(enemy.transform.position, runner.GetExecution(attack).Services.RequireTargetPosition());
            runner.TryCast(attack);
            var cast = runner.GetExecution(attack);
            Assert.IsNull(cast.Target); Assert.IsNull(cast.Services.Target); Assert.IsFalse(cast.Services.HasTargetPosition);
            Assert.AreSame(source.transform, cast.Services.Origin);
            // An unconfigured project cannot accidentally damage friends. Any remains compatible.
            var noTeams = new SkillRunner(ally.AbilitySystem, null);
            noTeams.Grant(attack); noTeams.TryCast(attack, new SkillCastTarget(enemy.AbilitySystem));
            Assert.AreEqual(40, HP(enemy));
        }

        [Test] public void DetachedProjectActionUsesOriginalTeamIdentityAfterSourceRecycle()
        {
            var owner = Unit("caster", Vector3.zero).AbilitySystem; var oldIdentity = owner.SourceIdentity;
            var enemy = Unit("enemy", Vector3.forward);
            var skill = Skill(new TravelBranchAction { afterTravel = new SkillAction[] {
                new ApplyEffectAction { effect = Effect(), targets = new TargetQuery { relation = TargetRelation.Enemy } } } });
            var runner = new SkillRunner(owner, new SkillUnitServices { SceneTasks = _scene,
                TargetRelations = (from, to, side) => from == oldIdentity && to == enemy.AbilitySystem && side == TargetRelation.Enemy });
            runner.Grant(skill); runner.TryCast(skill, new SkillCastTarget(enemy.AbilitySystem, enemy.transform));
            owner.Clear(); Assert.AreNotSame(oldIdentity, owner.SourceIdentity);
            _scene.Tick(1);
            Assert.AreEqual(90, enemy.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }

        [TestCase(true, 80)]
        [TestCase(false, 95)]
        public void ProjectActionCanHandOffToConfiguredEventSuccessOrTimeout(bool signal, int expectedHealth)
        {
            var owner = Unit("caster", Vector3.zero).AbilitySystem; var enemy = Unit("target", Vector3.forward);
            var skill = Skill(new TravelBranchAction { lifetime = SkillTaskOwnership.Ability,
                afterTravel = new SkillAction[] { new WaitSkillAction { kind = SkillWaitKind.GameplayEvent, eventTag = "Test.Other",
                    timeout = new SkillNumber(.5f), onSuccess = new SkillAction[] { new ApplyEffectAction { effect = Effect(-20) } },
                    onTimeout = new SkillAction[] { new ApplyEffectAction { effect = Effect(-5) } } } } });
            var runner = new SkillRunner(owner, null); runner.Grant(skill);
            runner.TryCast(skill, new SkillCastTarget(enemy.AbilitySystem));
            owner.Tick(.41f);
            if (signal) owner.Events.Send(new GameplayEvent("Test.Other", owner.SourceIdentity, new SkillParameterValue(1)));
            owner.Tick(signal ? .01f : .51f);
            Assert.AreEqual(expectedHealth, enemy.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.IsFalse(runner.IsActive(skill));
        }

        [Test] public void ProjectBranchDrivesGraphReferencesAndBothOwnershipChecks()
        {
            var branch = new TravelBranchAction { afterTravel = new SkillAction[]{new WaitSkillAction()} };
            var fixture = New<TravelFixtureAsset>(); fixture.stages = new SkillAction[]{branch};
            using var so = new SerializedObject(fixture);
            Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.ValidateStructure(so.FindProperty("stages")));
            Assert.Catch<SkillActionException>(()=>new SkillActionBuildContext().Build(fixture.stages));
            branch.lifetime=SkillTaskOwnership.Ability; so.Update();
            Assert.DoesNotThrow(()=>SkillFlowCommands.ValidateStructure(so.FindProperty("stages")));
            Assert.DoesNotThrow(()=>new SkillActionBuildContext().Build(fixture.stages));
            var hit = Effect(); branch.afterTravel = new SkillAction[]{new ApplyEffectAction{effect=hit}};
            var links = new List<AssetLink>(PortRules.Links(fixture));
            Assert.AreEqual(1,links.Count); Assert.AreSame(hit,links[0].Child);
            Assert.AreEqual("stages.Array.data[0].afterTravel.Array.data[0].effect",links[0].Port.ElementId);
            Assert.AreEqual("延迟落点",SkillFlowView.ActionTitle(branch));
            Assert.That(SkillActionFields.For(typeof(TravelBranchAction)).Inline, Does.Contain(("travel","飞行时长")));
        }

        [Test] public void ProjectPreviewAndSceneTaskAgreeOnDelayedHitWithLevelBoundTimeAndHierarchicalTags()
        {
            var source=Unit("caster",Vector3.zero); var victim=Unit("victim",Vector3.forward*2);
            var parent=GameplayTagManager.RequestTag("Test.VFX"); var child=GameplayTagManager.RequestTag("Test.VFX.Ground");
            victim.AbilitySystem.OwnedTags.AddTag(child);
            var p=new SkillParameter{levels=new[]{2f,.4f}};
            var action=new TravelBranchAction{travel=new SkillNumber(0){parameterId=p.Id},afterTravel=new SkillAction[]{
                new ApplyEffectAction{effect=Effect(),targets=new TargetQuery{kind=TargetQueryKind.FixedTarget,anyTags=new[]{parent}}}}};
            var skill=Skill(action); skill.parameters=new[]{p};
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASTravelPreview.asset");
            AssetDatabase.CreateAsset(skill,path);
            var preview=new PreviewRenderUtility();
            try {
                var runner=new SkillRunner(source.AbilitySystem,null); runner.Grant(skill,2);
                runner.TryCast(skill,victim.AbilitySystem,Services(source.transform,victim.transform));
                Assert.IsFalse(runner.IsActive(skill));
                using var doc=new SkillDocument();doc.Open(skill);
                using var sim=new SkillPreviewSimulation(preview){TargetTags="Test.VFX.Ground"};
                float time=0;
                foreach(float dt in new[]{.3f,.11f}) {
                    time+=dt; _scene.Tick(dt); doc.Playback.Seek(time,1);
                    sim.Sample(doc,source.transform,1,DummyLayout.直线,2,null,2,false,null);
                    bool arrived=time>.4f;
                    Assert.AreEqual(arrived?90:100,victim.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
                    Assert.AreEqual(arrived,sim.WasHit(0)); Assert.Greater(sim.End,.4f);
                }
                sim.TargetTags="Test.Other"; sim.Sample(doc,source.transform,1,DummyLayout.直线,2,null,2,false,null);
                Assert.IsFalse(sim.WasHit(0));
            } finally {preview.Cleanup();AssetDatabase.DeleteAsset(path);}
        }

        [Test] public void SceneTaskStartedByCancellationKeepsItsCastWhenSkillIsRecast()
        {
            var source=Unit("caster",Vector3.zero); var first=Unit("first",Vector3.forward); var second=Unit("second",Vector3.right);
            var skill=Skill(new WaitSkillAction{onCancel=new SkillAction[]{new TravelBranchAction{afterTravel=new SkillAction[]{
                new ApplyEffectAction{effect=Effect()}}}}});
            var runner=Granted(source.AbilitySystem,null,skill);
            runner.TryCast(skill,first.AbilitySystem,Services(source.transform,first.transform));
            var cancelled=runner.GetExecution(skill); cancelled.SetChannel("sample",7);
            source.AbilitySystem.EndAbility(runner.GetSpec(skill)); Assert.AreEqual(1,_scene.Count);
            runner.TryCast(skill,second.AbilitySystem,Services(source.transform,second.transform));
            Assert.AreNotSame(cancelled,runner.GetExecution(skill)); Assert.AreEqual(7,cancelled.Channels["sample"]);
            Assert.IsFalse(runner.GetExecution(skill).Channels.ContainsKey("sample"));
            _scene.Tick(1);
            Assert.AreEqual(90,first.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(100,second.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            source.AbilitySystem.EndAbility(runner.GetSpec(skill)); _scene.Tick(1);
            Assert.AreEqual(90,second.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }

        [Test] public void LastSceneCallbackCanRecastWithoutClearingTheFinishingCast()
        {
            var source=Unit("source",Vector3.zero);
            var skill=Skill(new TravelBranchAction{afterTravel=new SkillAction[]{new SendGameplayEventAction{tag=GameplayTagManager.RequestTag("Test.Cue")}}});
            var runner=Granted(source.AbilitySystem,null,skill); var services=Services(source.transform);
            runner.TryCast(skill,taskContext:services);
            var first=runner.GetExecution(skill); first.SetChannel("sample",9);
            source.AbilitySystem.Events.Received+=_=>runner.TryCast(skill,taskContext:services);
            _scene.Tick(1);
            Assert.AreNotSame(first,runner.GetExecution(skill)); Assert.AreEqual(9,first.Channels["sample"]);
            Assert.IsFalse(runner.GetExecution(skill).Channels.ContainsKey("sample"));
        }

        [Test] public void WarmParameterizedCastsWithRealCostAndSourceFormulaAllocateNothing()
        {
            var parameter = new SkillParameter { defaultValue = 2 };
            var owner = new AbilitySystemComponent();
            var health = owner.Attributes.Add(new AttributeId(1), 100);
            var attack = owner.Attributes.Add(new AttributeId(2), 5);
            var effect = Effect(parameter: parameter.Id);
            Edit(effect, so => {
                var m = so.FindProperty("modifiers.Array.data[0].magnitude");
                m.FindPropertyRelative("kind").enumValueIndex = (int)MagnitudeKind.Calculation;
                m.FindPropertyRelative("captureAttributeId").intValue = 2;
                m.FindPropertyRelative("coefficient").floatValue = 1;
                m.FindPropertyRelative("resultScale").floatValue = -1;
                m.FindPropertyRelative("maximum").floatValue = float.MaxValue;
            });
            var module = New<SkillActionModuleAsset>(); module.parameters = New<SkillParameterSetAsset>();
            module.parameters.parameters = new[] { parameter };
            module.actions = new SkillAction[] { new ApplyEffectAction { effect = effect, targets = new TargetQuery { kind = TargetQueryKind.Self } } };
            var skill = Skill(new CallModuleAction { module = module });
            var cost = Effect(-1); Edit(skill, so => so.FindProperty("cost").objectReferenceValue = cost);
            var runner = new SkillRunner(owner, null); runner.Grant(skill, 3);
            for (int i = 0; i < 100; i++) { health.BaseValue = 100; runner.TryCast(skill); }
            Assert.AreEqual(89, health.CurrentValue);
            Assert.IsTrue(AllocationMeter.Works()); using var meter = new AllocationMeter();
            for (int i = 0; i < 1000; i++) { health.BaseValue = 100; attack.BaseValue = i; runner.TryCast(skill); }
            long allocations = meter.Stop(); Assert.AreEqual(0, allocations);
            Assert.IsFalse(runner.IsActive(skill));
        }

        [Test] public void RepeatedOverridesStayWarmWithoutKeepingTemporaryArraysAlive()
        {
            var p = new SkillParameter { defaultValue = 1 };
            var skill = Skill(new ApplyEffectAction { effect = Effect(parameter: p.Id), targets = new TargetQuery { kind = TargetQueryKind.Self } });
            skill.parameters = new[] { p };
            var owner = new AbilitySystemComponent();
            var health = owner.Attributes.Add(new AttributeId(1), 100);
            var runner = Granted(owner, null, skill);
            var fixedOverrides = new[] { new SkillParameterOverride { parameterId = p.Id, value = 7 } };
            var temporary = new WeakReference[64];
            for (int i = 0; i < temporary.Length; i++) temporary[i] = CastOnce(runner, skill, p.Id, i);
            var otherOverrides = new[] { new SkillParameterOverride { parameterId = p.Id, value = 3 } };
            GC.Collect();
            Assert.Less(Array.FindAll(temporary, r => r.IsAlive).Length, 8, "The runner must not own one-off override arrays.");
            runner.TryCast(skill, parameterOverrides: fixedOverrides); runner.TryCast(skill, parameterOverrides: otherOverrides);
            Assert.IsTrue(AllocationMeter.Works());
            using var meter = new AllocationMeter();
            health.BaseValue = 5093;
            for (int i = 0; i < 1000; i++) runner.TryCast(skill, parameterOverrides: i % 2 == 0 ? fixedOverrides : otherOverrides);
            long allocations = meter.Stop();
            Assert.AreEqual(0, allocations);
            Assert.AreEqual(93, health.CurrentValue);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference CastOnce(SkillRunner runner, GameplayAbilityAsset skill, string id, int value)
        {
            var overrides = new[] { new SkillParameterOverride { parameterId = id, value = value } };
            runner.TryCast(skill, parameterOverrides: overrides);
            return new WeakReference(overrides);
        }

        [Test] public void FinishedSequenceStepsReleaseTheirModuleParameters()
        {
            var scale = new SkillParameter { defaultValue = 1 }; var local = new SkillParameter { defaultValue = 0 };
            var module = New<SkillActionModuleAsset>(); module.parameters = New<SkillParameterSetAsset>();
            module.parameters.parameters = new[] { local };
            module.actions = new SkillAction[] { new ApplyEffectAction { effect = Effect(parameter: local.Id) } };
            var values = new List<WeakReference>();
            SkillAction[] Step() => new SkillAction[] { new RememberParameters { Seen = values }, new CallModuleAction { module = module,
                bindings = new[] { new ModuleParameterBinding { callerParameterId = scale.Id, moduleParameterId = local.Id } } }, new NextTargetStepAction() };
            var skill = Skill(new TargetSequenceAction { maxTargets = new(3), allowRepeat = true, scaleParameterId = scale.Id,
                retention = new(.5f), first = Step(), next = Step() });
            skill.parameters = new[] { scale };
            var target = Unit("target", Vector3.forward); var owner = new AbilitySystemComponent();
            var runner = Granted(owner, null, skill);
            for (int i = 0; i < 20; i++) runner.TryCast(skill, target.AbilitySystem);
            Assert.AreEqual(65, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)), "Each cast hits for 1, 0.5, 0.25.");
            Assert.AreEqual(60, values.Count);
            GC.Collect();
            Assert.Less(values.FindAll(r => r.IsAlive).Count, 8, "Completed steps must not stay in the reusable cast's module cache.");
            GC.KeepAlive(runner);
        }
        private sealed class RememberParameters : SkillAction
        {
            public List<WeakReference> Seen;
            public override SkillActionDefinition Build(SkillActionBuildContext builder)
                => Define("observe", c => Seen.Add(new WeakReference(c.Values.Parameters)));
        }

        [Test] public void SharedModuleWaitsNavigateToTheCallThatStartedEachWait()
        {
            var inner = new RecordAim(); var module = New<SkillActionModuleAsset>();
            module.actions = new SkillAction[] { new WaitSkillAction { onSuccess = new SkillAction[] { inner } } };
            var a = new CallModuleAction { module = module }; var b = new CallModuleAction { module = module };
            var owner = Unit("owner", default); var skill = Skill(a, b); var runner = Granted(owner.AbilitySystem, null, skill);
            SkillEventRecorder.Acquire();
            try
            {
                SkillEventRecorder.Observe(owner.AbilitySystem);
                runner.TryCast(skill, taskContext: Services(owner.transform)); owner.AbilitySystem.Tick(1);
                var history = SkillEventRecorder.Of(owner.AbilitySystem); var calls = new List<string>();
                for (int i = 0; i < history.Count; i++)
                    if (history[i].Kind == TraceKind.动作开始 && history[i].RawId == inner.Id) calls.Add(history[i].NavigationId);
                CollectionAssert.AreEquivalent(new[] { a.Id, b.Id }, calls);
            }
            finally { SkillEventRecorder.Release(); }
        }

        [Test] public void SharedRecordsBelongToOneCastAndIgnoreAlreadySelectedTargetsBeforeTheLimit(){
            var caster=Unit("caster",new Vector3(48,0,48));
            var a=Unit("A",caster.transform.position+Vector3.right);var b=Unit("B",caster.transform.position+Vector3.right*2);
            Physics.SyncTransforms();var record="hits";var damage=Effect();
            SelectTargetsAction Pick()=>new(){targets=new TargetQuery{kind=TargetQueryKind.Circle,maxTargets=1,radius=new(4)},count=new(1),
                sharedRecord=record,recordSelection=true,actions=new SkillAction[]{new ApplyEffectAction{effect=damage}}};
            var asset=Skill(Pick(),Pick());var runner=new SkillRunner(caster.AbilitySystem,null);
            for(int cast=1;cast<=2;cast++){
            runner.Grant(asset);
            Assert.AreEqual(AbilityActivationResult.Activated,runner.TryCast(asset,taskContext:Services(caster.transform)));
                foreach(var unit in new[]{a,b})Assert.AreEqual(100-10*cast,unit.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            }
        }

        [Test] public void WaitingSkillCanQueryAnEventPointWithoutReplacingTheCasterOrigin(){
            var owner=Unit("owner",new Vector3(60,0,60));var target=Unit("target",owner.transform.position+Vector3.right*12);
            var effect=Effect();Physics.SyncTransforms();
            var wait=Skill(new WaitSkillAction{kind=SkillWaitKind.GameplayEvent,eventTag="Test.Other",onSuccess=new SkillAction[]{
                new SelectTargetsAction{targets=new TargetQuery{kind=TargetQueryKind.Circle,origin=SkillSpawnPosition.EventPosition,radius=new(1)},
                    actions=new SkillAction[]{new ApplyEffectAction{effect=effect}}}}});
            var runner=new SkillRunner(owner.AbilitySystem,null);var services=Services(owner.transform);
            runner.Grant(wait);
            Assert.AreEqual(AbilityActivationResult.Activated,runner.TryCast(wait,taskContext:services));
            services.TargetPosition=target.transform.position;
            var send=Skill(new SendGameplayEventAction{tag="Test.Other",includePosition=true,position=SkillSpawnPosition.TargetPoint});
            runner.Grant(send);
            Assert.AreEqual(AbilityActivationResult.Activated,runner.TryCast(send,taskContext:services));
            owner.AbilitySystem.Tick(.05f);
            Assert.AreEqual(90,target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(100,owner.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void SequenceScopesSurviveEachProjectileAndNeverLoopBackToTheCaster(){
            var caster=Unit("caster",new Vector3(42,0,42));var a=Unit("A",caster.transform.position+Vector3.right);
            var b=Unit("B",a.transform.position+Vector3.right);var c=Unit("C",b.transform.position+Vector3.right);Physics.SyncTransforms();
            var index=new SkillParameter{kind=SkillParameterKind.Int,defaultValue=0};
            var damage=Effect(parameter:index.Id);var prefab=Go("visual");
            var pick=new SelectTargetsAction{targets=new TargetQuery{origin=SkillSpawnPosition.HitPoint,kind=TargetQueryKind.Circle,radius=new(1.6f),maxTargets=1},
                actions=new SkillAction[]{new NextTargetStepAction()}};
            LaunchProjectileAction Fire(SkillSpawnPosition origin)=>new(){prefab=prefab,speed=new(8),radius=.05f,origin=origin,onlySelectedTarget=true,
                onHit=new SkillAction[]{new ApplyEffectAction{effect=damage},pick}};
            // Distinct authoring instances: stable IDs must not be reused at two sites.
            var initial=Fire(SkillSpawnPosition.Caster);var next=Fire(SkillSpawnPosition.HitPoint);
            next.onHit[1]=new SelectTargetsAction{targets=pick.targets,actions=new SkillAction[]{new NextTargetStepAction()}};
            var sequence=new TargetSequenceAction{maxTargets=new(3),indexParameterId=index.Id,first=new SkillAction[]{initial},next=new SkillAction[]{next}};
            var asset=Skill(sequence);asset.parameters=new[]{index};
            var runner=new SkillRunner(caster.AbilitySystem,null);var services=Services(caster.transform,a.transform);
            // Two independent uses of the same asset can overlap after the source ability ends.
            runner.Grant(asset); for(int run=0;run<2;run++)Assert.AreEqual(AbilityActivationResult.Activated,runner.TryCast(asset,a.AbilitySystem,services));
            for(int tick=0;tick<15;tick++)_scene.Tick(.05f);
            Assert.AreEqual(0,_scene.Count);Assert.AreEqual(100,a.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
            Assert.AreEqual(98,b.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));Assert.AreEqual(96,c.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }

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
                var runner=new SkillRunner(owner.AbilitySystem,null);runner.Grant(skill); runner.TryCast(skill,points[2].AbilitySystem,Services(owner.transform,points[2].transform));
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
        [Test] public void SelectedQueryUsesCallBindingsAndDoesNotDependOnPlayheadOrDummies()
        {
            var outer=new SkillParameter{levels=new[]{2f,8f}};
            var local=new SkillParameter{defaultValue=1};
            var area=new CreateAreaAction{targets=new TargetQuery{kind=TargetQueryKind.Sector,radius=new SkillNumber(0){parameterId=local.Id}}};
            var module=New<SkillActionModuleAsset>();module.parameters=New<SkillParameterSetAsset>();module.parameters.parameters=new[]{local};module.actions=new SkillAction[]{area};
            var cast=Skill(new CallModuleAction{module=module,bindings=new[]{new ModuleParameterBinding{moduleParameterId=local.Id,callerParameterId=outer.Id}}});
            cast.parameters=new[]{outer};
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASSelectedRange.asset");AssetDatabase.CreateAsset(cast,path);
            try{
                using var doc=new SkillDocument();doc.Open(cast,false);
                Assert.IsNull(SelectedActionRange.Read(doc,2,out _,out _));
                doc.Selection.Select(module,"@field","actions.Array.data[0]");
                var shape=SelectedActionRange.Read(doc,2,out var origin,out _);
                Assert.AreEqual(SkillSpawnPosition.Caster,origin);Assert.AreEqual(8,shape.Value.Radius);
                area.targets.origin=SkillSpawnPosition.EventPosition;
                Assert.IsNull(SelectedActionRange.Read(doc,2,out _,out var note));Assert.IsNotEmpty(note);
                doc.Selection.Select(cast);Assert.IsNull(SelectedActionRange.Read(doc,2,out _,out _));
            }finally{AssetDatabase.DeleteAsset(path);}
        }
        [Test] public void GeneratedEnergyBoltPreviewShowsSimultaneousForksOrSuccessiveArrivals(){
            var root=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>("Assets/GAS Samples/BasicCombat/Skills/分叉闪电.asset");
            Assert.NotNull(root,"Generate BasicCombat in the validation project.");
            var preview=new PreviewRenderUtility();var caster=Go("preview caster");
            try{
                using var doc=new SkillDocument();doc.Open(root,assignMissingIds:false);
                using var sim=new SkillPreviewSimulation(preview){AimIndex=1};
                var bounce=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>("Assets/GAS Samples/BasicCombat/Skills/逐个弹射.asset");
                Assert.AreSame(root.parameterSet,bounce.parameterSet);Assert.AreSame(root.Definition.Cooldown,bounce.Definition.Cooldown);
                var contacts=new List<SkillPreviewContact>();
                void Sample(bool fork,bool relay=false,float time=0){
                    var selected=fork?root:bounce;if(doc.Root!=selected)doc.Open(selected,false);
                    doc.Playback.Seek(time,10);contacts.Clear();
                    var points=new Vector3[3];
                    for(int i=0;i<3;i++)points[i]=relay?new Vector3(i%2==0?0:.7f,0,2*(1+i)):SkillPreviewSimulation.Position(DummyLayout.直线,i,3,2);
                    sim.ProjectContacts(doc,points,Vector3.forward*2,1,null,contacts.Add);
                    contacts.Sort((a,b)=>a.Arrival.CompareTo(b.Arrival));
                }
                foreach(bool fork in new[]{true,false}){
                    Sample(fork,false);
                    Assert.AreEqual(0,sim.HitCount,"The route overview must not fire future impacts.");
                    Assert.AreEqual(3,contacts.Count);
                    var a=contacts[0];var b=contacts[1];var c=contacts[2];
                    Assert.AreEqual(-1,a.FromUnit);Assert.AreEqual(0,a.ToUnit);Assert.IsFalse(a.Action is ShowTargetLinkAction);
                    Assert.Greater(a.Arrival,root.Animation().Duration);Assert.AreEqual(a.Arrival,b.Launch);
                    var body=SkillPreviewSimulation.Position(DummyLayout.直线,0,3,2)+Vector3.up*SkillPreviewSimulation.DummyRadius;
                    Assert.AreEqual(SkillPreviewSimulation.DummyRadius,(body-a.To).magnitude,.0001f);
                    Assert.AreEqual(a.To,b.From);Assert.AreEqual(a.ToUnit,b.FromUnit);Assert.AreNotEqual(b.ToUnit,c.ToUnit);
                    if(fork){
                        Assert.IsTrue(b.Action is ShowTargetLinkAction && c.Action is ShowTargetLinkAction);Assert.AreEqual(a.To,c.From);Assert.AreEqual(0,c.FromUnit);
                        Assert.AreEqual(a.Arrival,b.Arrival);Assert.AreEqual(a.Arrival,c.Arrival);
                    }else{
                        Assert.IsFalse(b.Action is ShowTargetLinkAction || c.Action is ShowTargetLinkAction);Assert.AreEqual(b.To,c.From);Assert.AreEqual(b.ToUnit,c.FromUnit);
                        Assert.AreEqual(b.Arrival,c.Launch);Assert.Greater(b.Arrival,a.Arrival);Assert.Greater(c.Arrival,b.Arrival);
                    }
                    Assert.Greater(sim.End,c.Arrival,"Looping playback must have time to show the last impact.");
                    Sample(fork,false,a.Arrival);Assert.AreEqual(fork?3:1,sim.HitCount);
                    Sample(fork,false,c.Arrival);Assert.IsTrue(sim.WasHit(c.ToUnit));
                    Sample(fork,false,0);Assert.AreEqual(c.Arrival,contacts[2].Arrival);Assert.AreEqual(0,sim.HitCount);
                }
                Sample(true,true);Assert.AreEqual(2,contacts.Count,"#3 is outside the first contact's search radius.");
                Sample(false,true);Assert.AreEqual(3,contacts.Count,"#2 can reach #3 on its own impact.");
            }finally{preview.Cleanup();}
        }
        [Test] public void EnergyBoltRouteControlsSwitchPreviewOnlyAndSeekPastTheAnimation(){
            var root=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>("Assets/GAS Samples/BasicCombat/Skills/分叉闪电.asset");
            Assert.NotNull(root);
            var bounce=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>("Assets/GAS Samples/BasicCombat/Skills/逐个弹射.asset");
            var window=ZFramework.GAS.Samples.BasicCombat.Editor.EnergyBoltPreviewWindow.Open(root);
            try{
                List<Button> Stops()=>window.rootVisualElement.Query<Button>().ToList().FindAll(b=>b.userData is float);
                var stops=Stops();Assert.AreEqual(3,stops.Count);Assert.IsTrue(stops[2].text.Contains("闪电"));
                Assert.AreEqual(stops[0].userData,stops[2].userData);
                window.ShowAsset(bounce);
                stops=Stops();Assert.AreEqual(3,stops.Count);Assert.IsTrue(stops[2].text.Contains("飞弹"));
                float last=(float)stops[2].userData;Assert.Greater(last,(float)stops[1].userData);
                Assert.Greater(last,root.Animation().Duration);
                using(var submit=NavigationSubmitEvent.GetPooled()){submit.target=stops[2];stops[2].SendEvent(submit);}
                Assert.AreEqual(last,window.Time,.00001f);
            }finally{window.Close();}
        }
        [Test] public void EffectOnlyParameterSurvivesModuleExtractionRemappingAndInlining()
        {
            var p=new SkillParameter{defaultValue=7};var alternate=new SkillParameter{defaultValue=19};
            var effect=Effect(parameter:p.Id);
            var root=Skill(new ApplyEffectAction{effect=effect,targets=new TargetQuery{kind=TargetQueryKind.Self}});root.parameters=new[]{p,alternate};
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectScopeTest.asset");
            var modulePath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectScopeModule.asset");
            var effectPath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASSharedScopeEffect.asset");
            AssetDatabase.CreateAsset(root,path);AssetDatabase.CreateAsset(effect,effectPath);
            try
            {
                using var doc=new SkillDocument();doc.Open(root);
                float Cast(){
                    var asc=new AbilitySystemComponent();asc.Attributes.Add(new AttributeId(1),100);
                    var runner=new SkillRunner(asc,null);runner.Grant(root); runner.TryCast(root);return asc.Attributes.GetCurrent(new AttributeId(1));
                }
                Assert.AreEqual(93,Cast());
                SkillFlowCommands.Extract(doc,root,"activationActions",0,1,modulePath,_=>true);
                var call=(CallModuleAction)root.activationActions[0];Assert.AreEqual(p.Id,call.bindings[0].callerParameterId);
                Assert.AreEqual(93,Cast());call.bindings[0].callerParameterId=alternate.Id;root.InvalidateDefinition();
                Assert.AreEqual(81,Cast()); SkillFlowCommands.Inline(doc,root,"activationActions",0,_=>true);
                Assert.IsInstanceOf<InlineParameterScopeAction>(root.activationActions[0]);Assert.AreEqual(81,Cast());
                var owned=((ApplyEffectAction)((InlineParameterScopeAction)root.activationActions[0]).actions[0]).effect;
                Assert.AreSame(((ApplyEffectAction)call.module.actions[0]).effect,owned);
                Assert.AreEqual(effectPath,AssetDatabase.GetAssetPath(owned));
                AssetDatabase.DeleteAsset(modulePath);Assert.AreEqual(81,Cast(),"Inlined effects must outlive the module file.");
                CollectionAssert.DoesNotContain(AssetDatabase.GetDependencies(path),modulePath);
            }finally{AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(modulePath);AssetDatabase.DeleteAsset(effectPath);}
        }

        private sealed class RecordAim : SkillAction
        {
            public readonly List<Vector3> Points = new(2048);
            public override SkillActionDefinition Build(SkillActionBuildContext builder)
                => Define("record", context => Points.Add(context.Services.RequireTargetPosition()));
        }
        [Test] public void ParameterEventAndRepeatedModuleScopesPreserveExplicitAimWithoutPeriodicAllocations()
        {
            var owner = Unit("owner", Vector3.zero); var target = Unit("target", Vector3.forward * 9);
            var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(100) });
            var runner = new SkillRunner(owner.AbilitySystem, null); var services = Services(owner.transform, target.transform);
            services.TargetPosition = Vector3.right * 7;runner.Grant(skill);  runner.TryCast(skill, target.AbilitySystem, services);
            var context = runner.GetExecution(skill);
            Assert.AreEqual(services.TargetPosition, context.WithParameters(SkillParameterValues.Empty).Services.TargetPosition);
            Assert.AreEqual(services.TargetPosition, context.WithEvent(default).Services.TargetPosition);
            var spy = new RecordAim(); var module = New<SkillActionModuleAsset>(); module.actions = new SkillAction[] { spy };
            var action = new CallModuleAction { module = module }.Build(new SkillActionBuildContext());
            context.ExecuteAction(action);
            context.Services.TargetPosition = Vector3.left * 4; context.ExecuteAction(action);
            CollectionAssert.AreEqual(new[] { Vector3.right * 7, Vector3.left * 4 }, spy.Points);
            Assert.IsTrue(AllocationMeter.Works()); using var meter = new AllocationMeter();
            for (int i = 0; i < 1000; i++) context.ExecuteAction(action);
            long count = meter.Stop(); Assert.AreEqual(0, count, "Warm synchronous module scopes must reuse their leased frames.");
            runner.Cancel(skill);
        }
        [Test] public void ModuleWaitCapturesAimBeforeReusableModuleFrameChanges()
        {
            var owner = Unit("owner", default); var runner = new SkillRunner(owner.AbilitySystem, null);
            var skill = Skill(new WaitSkillAction { timeout = new SkillNumber(100) });runner.Grant(skill);  runner.TryCast(skill, taskContext: Services(owner.transform));
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
            skill.parameterOverrides = Array.Empty<SkillParameterOverride>(); skill.InvalidateDefinition(); runner.TryCast(skill);
            Assert.AreEqual(10, owner.Attributes.GetCurrent(new AttributeId(1)));
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
            var runner = new SkillRunner(owner.AbilitySystem); new SkillTimelinePlayer(runner, () => new Driver());
            Physics.SyncTransforms();runner.Grant(skill);  runner.TryCast(skill, target.AbilitySystem, Services(owner.transform, target.transform, pool));
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
            Physics.SyncTransforms(); var skill = Skill(); var runner = new SkillRunner(owner.AbilitySystem, null);runner.Grant(skill);  runner.TryCast(skill, near.AbilitySystem, Services(owner.transform, near.transform));
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
            runner.Grant(skill);
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
            runner.Grant(skill);
            runner.TryCast(skill); owner.Tick(.1f); owner.Tick(.1f);
            Assert.AreEqual(90, owner.Attributes.GetCurrent(new AttributeId(1))); Assert.IsFalse(runner.IsActive(skill));
            skill.activationActions = new SkillAction[] { wait }; skill.InvalidateDefinition(); runner.TryCast(skill); runner.Cancel(skill);
            owner.Events.Send(new GameplayEvent("Test.Other", owner, new SkillParameterValue(0))); owner.Tick(3);
            Assert.AreEqual(90, owner.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void IndependentSpawnHostClearReturnsObjectsExactlyOnce()
        {
            var owner = Unit("source", Vector3.zero); var prefab = Go("prefab"); prefab.SetActive(false); var pool = new Pool();
            var skill = Skill(new SpawnPrefabAction { prefab = prefab }); var runner = new SkillRunner(owner.AbilitySystem, null);
            runner.Grant(skill);
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
            runner.Grant(skill);
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
            Granted(owner, null, skill).TryCast(skill, taskContext: new TimelineTaskContext { TargetSelection = service });
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
            var runner = new SkillRunner(owner, null);runner.Grant(skill);  runner.TryCast(skill);
            parameter.defaultValue = 20; Edit(cost, so => so.FindProperty("modifiers.Array.data[0].magnitude.constant").floatValue = -2);
            cost.InvalidateDefinition(); skill.InvalidateDefinition(); owner.Tick(.2f); owner.Tick(0);
            Assert.AreEqual(89, owner.Attributes.GetCurrent(new AttributeId(1)));
            runner.TryCast(skill); owner.Tick(.2f); owner.Tick(0);
            Assert.AreEqual(67, owner.Attributes.GetCurrent(new AttributeId(1)));
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
            a.Grant(skill); a.TryCast(skill); b.TryCast(skill);
            Assert.AreEqual(90, first.Attributes.GetCurrent(new AttributeId(1))); Assert.AreEqual(80, second.Attributes.GetCurrent(new AttributeId(1)));
            module.name = "rename"; shared.displayName = "renamed"; a.TryCast(skill);
            Assert.AreEqual(80, first.Attributes.GetCurrent(new AttributeId(1)));
        }
        [Test] public void InvalidModuleBindingFailsBeforeGrant()
        {
            var local = new SkillParameter { kind = SkillParameterKind.Int };
            var set = New<SkillParameterSetAsset>(); set.parameters = new[] { local };
            var module = New<SkillActionModuleAsset>(); module.parameters = set;
            var caller = new SkillParameter { kind = SkillParameterKind.Bool };
            var call = new CallModuleAction { module = module, bindings = new[] { new ModuleParameterBinding { callerParameterId = caller.Id, moduleParameterId = local.Id } } };
            var skill = Skill(call); skill.parameters = new[] { caller };
            Assert.AreEqual(call.Id, Assert.Throws<SkillActionException>(() => skill.BuildActions()).ActionId);
            call.bindings[0].moduleParameterId = "deleted";
            StringAssert.Contains("模块参数不存在", Assert.Throws<SkillActionException>(() => skill.BuildActions()).Message);
        }
        [Test] public void BranchAndJumpIndexRejectWrongParameterKindsWhenBuilt()
        {
            var number = new SkillParameter { kind = SkillParameterKind.Float };
            foreach (var action in new SkillAction[] { new ConditionalAction { booleanParameterId=number.Id }, new TargetSequenceAction { indexParameterId=number.Id } })
            {
                var skill = Skill(action); skill.parameters = new[] { number };
                Assert.AreEqual(action.Id, Assert.Throws<SkillActionException>(()=>skill.BuildActions()).ActionId);
            }
        }
        [Test] public void WarmQueriesAndIdleSceneTicksDoNotAllocate()
        {
            var source = Unit("source", Vector3.zero); Unit("target", Vector3.forward);
            var skill = Skill(new CreateAreaAction { duration = new SkillNumber(10000), period = new SkillNumber(0) });
            var runner = new SkillRunner(source.AbilitySystem, null); Physics.SyncTransforms();runner.Grant(skill);  runner.TryCast(skill, taskContext: Services(source.transform));
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
            Physics.SyncTransforms();runner.Grant(skill);  runner.TryCast(skill, taskContext: Services(source.transform)); Assert.AreEqual(1, received);
        }
        [Test] public void SaturatedEventQueryFailsBeforeSendingAnything()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward);
            for (int i = 0; i < 256; i++) target.gameObject.AddComponent<BoxCollider>();
            int received = 0; target.AbilitySystem.Events.Received += _ => received++;
            var skill = Skill(new SendGameplayEventAction { tag = "Test.Other", targets = new TargetQuery { kind = TargetQueryKind.Circle } });
            Physics.SyncTransforms(); Assert.Throws<InvalidOperationException>(() => Granted(source.AbilitySystem, null, skill).TryCast(skill, taskContext: Services(source.transform)));
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
            runner.Grant(skill);
            Assert.DoesNotThrow(() => runner.TryCast(skill, taskContext: Services(source.transform, pool: pool)));
            Assert.AreEqual(1, pool.Created); Assert.AreEqual(1, pool.Returned); Assert.AreEqual(0, _scene.Count);
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
        [Test] public void AreaCapKeepsAdmittedOccupantsUntilTheyActuallyLeave()
        {
            var source = Unit("source", Vector3.zero); var first = Unit("first", Vector3.forward); var second = Unit("second", Vector3.forward * 2);
            var effect = Effect(0); Edit(effect, so => { so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration; so.FindProperty("duration").floatValue = 100; });
            var skill = Skill(new CreateAreaAction { duration = new SkillNumber(20), period = new SkillNumber(0), enterEffect = effect,
                targets = new TargetQuery { kind = TargetQueryKind.Circle, maxTargets = 1, radius = new SkillNumber(3) } });
            Physics.SyncTransforms(); Granted(source.AbilitySystem, null, skill).TryCast(skill, taskContext: Services(source.transform)); _scene.Tick(.1f);
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
        [Test] public void WarmPeriodicModuleBindingsDoNotAllocate()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward);
            var module = New<SkillActionModuleAsset>(); module.actions = new SkillAction[] { new SendGameplayEventAction { tag = "Test.Other" } };
            var skill = Skill(new CreateAreaAction { duration = new SkillNumber(10000), period = new SkillNumber(.01f), onPeriod = new SkillAction[] { new CallModuleAction { module = module } } });
            Physics.SyncTransforms(); Granted(source.AbilitySystem, null, skill).TryCast(skill, taskContext: Services(source.transform));
            for (int i = 0; i < 100; i++) _scene.Tick(.02f);
            Assert.IsTrue(AllocationMeter.Works()); using var meter = new AllocationMeter();
            for (int i = 0; i < 1000; i++) _scene.Tick(.02f);
            long allocated = meter.Stop(); Assert.AreEqual(0, allocated);
        }
        private sealed class SourceRequiredFilter : ITargetFilter
        { public bool Allows(AbilitySystemComponent source, AbilitySystemComponent target) => source.Attributes.Count > 0; }
        private sealed class ThrowingSelection : ISkillTargetSelection
        {
            public IDisposable Request(SkillExecutionContext context, Action<AbilitySystemComponent, Transform> selected)
                => throw new NullReferenceException("picker startup");
        }
        [TestCase(false)]
        [TestCase(true)]
        public void DirectAnimationHitUsesCastSourcePolicy(bool frozen)
        {
            var source = Unit("source", Vector3.right * 30); var target = Unit("target", source.transform.position + Vector3.forward);
            var owner = source.AbilitySystem; var health = new AttributeId(1); var attack = new AttributeId(2);
            var value = owner.Attributes.Add(attack, 5); var ability = owner.GrantAbility(new GameplayAbilityDefinition());
            owner.TryActivateAbility(ability, frozen ? new EffectEvaluationContext(ability.Level, source: owner.Attributes) : null);
            var damage = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
                new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.FromAttribute(attack, -1, MagnitudeCapture.Source)) });
            value.BaseValue = 11;
            var task = new AnimationTask(new SkillAnimationDefinition("a", 1, markers: new[] {
                new AnimationMarker(.1f, actions: new[] { ApplyEffectAction.FromDefinition("hit", damage, new TargetQuery { kind = TargetQueryKind.FixedTarget }.Build()) }) }), new Driver(), target.AbilitySystem, 1);
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
                => Define("delayed fault", _ => throw new InvalidOperationException("delayed module failure"));
        }
        [Test] public void RecycledSourceDoesNotReachNonSnapshotFilterAsNull()
        {
            var source = Unit("source", Vector3.zero); var target = Unit("target", Vector3.forward); var services = Services(source.transform, target.transform);
            services.TargetFilter = new SourceRequiredFilter();
            var skill = Skill(new CreateAreaAction { period = new SkillNumber(.01f), onPeriod = new SkillAction[] { new ApplyEffectAction { effect = Effect() } } });
            Physics.SyncTransforms(); Granted(source.AbilitySystem, null, skill).TryCast(skill, taskContext: services); source.AbilitySystem.Clear();
            Assert.DoesNotThrow(() => _scene.Tick(.1f)); Assert.AreEqual(100, target.AbilitySystem.Attributes.GetCurrent(new AttributeId(1)));
        }
    }
}
