using System.Collections;
using System.Linq;
using GameplayTags;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class CueVfxPlaybackTests
    {
        private const string CueTag = "Test.VFX.Ground";
        private static void Tags() => GameplayTagManager.Initialize(new[] { new GameplayTagRegistration(CueTag) });
        [UnityTest]
        public IEnumerator ParticleCueFollowsPolicyAndAutomaticallyReclaimsItsTail()
        {
            Tags();
            var tag = GameplayTagManager.RequestTag(CueTag);
            Assert.IsTrue(tag.IsValid);
            var prefab = new GameObject("Particle template"); prefab.SetActive(false);
            var particles = prefab.AddComponent<ParticleSystem>();
            var main = particles.main; main.playOnAwake = false; main.startLifetime = .2f; main.startSpeed = 0; main.loop = true;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = particles.emission; emission.rateOverTime = 80;
            var library = ScriptableObject.CreateInstance<CueVfxLibrary>();
            library.bindings = new[] { new CueVfxBinding { cueTag = tag, prefab = prefab, follow = false,
                stopMode = CueVfxStopMode.StopEmitting, tailSeconds = .5f } };
            var hero = new GameObject("VFX test hero");
            var router = new GameplayCueRouter(); var asc = new AbilitySystemComponent(cueRouter: router);
            var player = hero.AddComponent<CueVfxPlayer>(); player.SetLibrary(library); player.Bind(asc);
            GameObject[] Instances() => Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Where(t => t.name == "CueVFX · " + tag.Name).Select(t => t.gameObject).ToArray();
            void Cue(GameplayCueEvent e) => router.Dispatch(new GameplayCueParameters(tag, e, asc, asc, 1));
            try
            {
                Cue(GameplayCueEvent.OnActive); yield return new WaitForSeconds(.12f);
                var root = Instances().Single(); var instance = root.GetComponentInChildren<ParticleSystem>();
                Assert.Greater(instance.particleCount, 0);
                hero.transform.position = Vector3.right * 4; yield return null;
                Assert.AreEqual(Vector3.zero, root.transform.position);
                Cue(GameplayCueEvent.Removed); Assert.IsFalse(instance.isEmitting); Assert.IsTrue(root.activeSelf);
                yield return new WaitForSeconds(.6f); Assert.IsTrue(root == null); Assert.IsEmpty(Instances());
                Cue(GameplayCueEvent.OnActive); player.enabled = false; yield return null;
                Assert.IsEmpty(Instances()); Assert.AreEqual(0, router.HandlerCount);
            }
            finally { Object.Destroy(hero); Object.Destroy(prefab); Object.Destroy(library); }
        }

    }
}
