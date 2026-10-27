using System;
using System.Collections;
using GameplayTags;
using NUnit.Framework;
using Spine.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Unity.Spine;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class SpineBackendTests
    {
        [UnityTest]
        public IEnumerator Spine42PlaybackStretchMarkersAndStop()
        {
            var json = new TextAsset("{\"skeleton\":{\"hash\":\"test\",\"spine\":\"4.2.0\"},\"bones\":[{\"name\":\"root\"}],\"animations\":{\"Attack\":{\"bones\":{\"root\":{\"rotate\":[{\"value\":0},{\"time\":0.8,\"value\":30}]}}}}}");
            var data = SkeletonDataAsset.CreateRuntimeInstance(json, Array.Empty<AtlasAssetBase>(), true, 1);
            var go = new GameObject("Spine backend test");
            var skeleton = go.AddComponent<SkeletonAnimation>(); skeleton.skeletonDataAsset = data; skeleton.Initialize(true);
            var animator = go.AddComponent<SkillAnimator>(); animator.SetBackend(new SpineBackend { mixDuration = 0 });
            try
            {
                yield return null;
                var driver = animator.Driver;
                Assert.AreEqual(.4f, driver.Play("Attack", .4f, false), .001f);
                Assert.AreEqual(2, skeleton.AnimationState.GetCurrent(0).TimeScale, .001f);
                skeleton.Update(.2f); Assert.AreEqual(15, skeleton.Skeleton.RootBone.Rotation, .2f);
                driver.Stop(); Assert.AreEqual("<empty>", skeleton.AnimationState.GetCurrent(0).Animation.Name);
                Assert.AreEqual(0, driver.Play("Missing", 1, false));
                GameplayTagManager.Initialize(Array.Empty<GameplayTagRegistration>());
                var owner = new AbilitySystemComponent(); var ability = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(ability);
                float hit = -1, elapsed = 0;
                var task = new AnimationTask(new SkillAnimationDefinition("Attack", .8f, markers: new[] { new AnimationMarker(.4f) }),
                    driver, durationOverride: .4f, onMarker: (_, __) => hit = elapsed);
                owner.StartTask(ability, task);
                for (int i = 0; i < 13; i++) { elapsed += 1f / 60; owner.Tick(1f / 60); }
                Assert.AreEqual(.2f, hit, 1f / 60); owner.EndAbility(ability);
            }
            finally { Object.Destroy(go); Object.Destroy(data); Object.Destroy(json); }
        }
    }
}
