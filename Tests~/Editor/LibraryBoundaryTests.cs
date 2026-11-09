using System;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class LibraryBoundaryTests
    {
        [Test]
        public void FixedTargetsNeedNeitherPhysicsNorAHiddenSceneHost()
        {
            var source = new AbilitySystemComponent();
            var target = new AbilitySystemComponent();
            var ability = source.GrantAbility(new GameplayAbilityDefinition());
            source.TryActivateAbility(ability);
            var context = new SkillExecutionContext(null, ability, new SkillCastTarget(target), new EffectEvaluationContext(1), null);
            var query = new TargetQuery().Build();
            var buffer = context.RentBuffer();
            try
            {
                Assert.AreEqual(1, context.Services.Searcher.Query(query, context, buffer));
                Assert.AreSame(target, buffer.Units[0]);
                Assert.IsNull(context.Services.SceneTasks);
            }
            finally { context.ReturnBuffer(buffer); source.Clear(); target.Clear(); }
        }

        [Test]
        public void SpatialQueriesRequireAnExplicitAdapter()
        {
            var owner = new AbilitySystemComponent();
            var ability = owner.GrantAbility(new GameplayAbilityDefinition());
            owner.TryActivateAbility(ability);
            var context = new SkillExecutionContext(null, ability, default, new EffectEvaluationContext(1), null);
            var buffer = context.RentBuffer();
            try
            {
                var query = new TargetQuery { kind = TargetQueryKind.Circle }.Build();
                Assert.Throws<InvalidOperationException>(() => context.Services.Searcher.Query(query, context, buffer));
            }
            finally { context.ReturnBuffer(buffer); owner.Clear(); }
        }

        [Test]
        public void PhysicsQueriesUseTheInjectedSceneAndReuseItsBuffer()
        {
            var scene = SceneManager.CreateScene("GAS Query " + Guid.NewGuid(), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var local = new GameObject("Local target");
            var decoy = new GameObject("Other scene target");
            var owner = new AbilitySystemComponent();
            var ability = owner.GrantAbility(new GameplayAbilityDefinition());
            owner.TryActivateAbility(ability);
            try
            {
                SceneManager.MoveGameObjectToScene(local, scene);
                var unit = local.AddComponent<SkillActionTestUnit>();
                local.AddComponent<SphereCollider>();
                decoy.AddComponent<SkillActionTestUnit>(); decoy.AddComponent<SphereCollider>();
                scene.GetPhysicsScene().Simulate(.02f);
                Physics.SyncTransforms();
                var searcher = new PhysicsTargetSearcher();
                var context = new SkillExecutionContext(null, ability, default, new EffectEvaluationContext(1),
                    new SkillUnitServices { Scene = scene, Searcher = searcher });
                var query = new TargetQuery { kind = TargetQueryKind.Circle }.Build();
                var buffer = context.RentBuffer();
                Assert.AreEqual(1, searcher.Query(query, context, buffer));
                Assert.AreSame(unit.AbilitySystem, buffer.Units[0]);
                context.ReturnBuffer(buffer);
                Assert.AreSame(buffer, context.RentBuffer());
                Assert.AreEqual(0, buffer.Count);
                context.ReturnBuffer(buffer);
            }
            finally
            {
                owner.Clear();
                UnityEngine.Object.DestroyImmediate(local);
                UnityEngine.Object.DestroyImmediate(decoy);
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
