using System;
using System.Collections.Generic;
using System.Linq;
using GameplayTags;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class CueVfxTests
    {
        private readonly List<Object> _objects = new();
        private GameplayTag _tag;
        private GameObject _hero, _prefab;
        private CueVfxLibrary _library;
        private GameplayCueRouter _router;
        private AbilitySystemComponent _owner;
        private CueVfxPlayer _player;

        [SetUp]
        public void Setup()
        {
            TestTags.Initialize();
            _tag = GameplayTagManager.RequestTag("Test.VFX.Ground");
            _hero = Keep(new GameObject("Cue test owner"));
            var mount = new GameObject("Mount").transform; mount.SetParent(_hero.transform, false); mount.localPosition = Vector3.right;
            _prefab = Keep(new GameObject("Effect")); _prefab.SetActive(false);
            _library = Keep(ScriptableObject.CreateInstance<CueVfxLibrary>());
            _library.bindings = new[] { new CueVfxBinding { cueTag = _tag, prefab = _prefab, anchorPath = "Mount", follow = false } };
            _router = new GameplayCueRouter(); _owner = new AbilitySystemComponent(cueRouter: _router);
            _player = _hero.AddComponent<CueVfxPlayer>(); _player.SetLibrary(_library); _player.Bind(_owner);
        }
        private T Keep<T>(T obj) where T : Object { _objects.Add(obj); return obj; }
        private static GameObject[] Instances() => Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t != null && t.name.StartsWith("CueVFX · ") && !EditorUtility.IsPersistent(t)).Select(t => t.gameObject).ToArray();
        private void Cue(GameplayCueEvent e, AbilitySystemComponent target = null)
            => _router.Dispatch(new GameplayCueParameters(_tag, e, target ?? _owner, _owner, 1));
        [Test]
        public void PreviewReportsMissingPlayerLibraryAndBindings()
        {
            var bare = Keep(new GameObject("Unconfigured character"));
            using (var preview = new CueVfxPreview(bare)) StringAssert.Contains("Cue VFX Player", preview.ConfigurationWarning(_tag.Name));
            _player.SetLibrary(null);
            using (var preview = new CueVfxPreview(_hero)) StringAssert.Contains("未指定特效映射库", preview.ConfigurationWarning(_tag.Name));
            _player.SetLibrary(_library);
            using (var preview = new CueVfxPreview(_hero))
            {
                Assert.IsNull(preview.ConfigurationWarning(_tag.Name));
                StringAssert.Contains("未配置 Cue", preview.ConfigurationWarning("Test.Other"));
                _library.bindings[0].prefab = null;
                StringAssert.Contains("Prefab", preview.ConfigurationWarning(_tag.Name));
                _library.bindings[0].prefab = _prefab; _library.bindings[0].anchorPath = "Missing";
                StringAssert.Contains("挂点不存在", preview.ConfigurationWarning(_tag.Name));
            }
        }
        [TearDown]
        public void Cleanup()
        {
            _player.Bind(null);
            foreach (var obj in Instances()) Object.DestroyImmediate(obj);
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void WorldAnchorStaysPutAndFollowingAnchorMoves()
        {
            _library.bindings = new[] { _library.bindings[0], new CueVfxBinding { cueTag = _tag, prefab = _prefab, anchorPath = "Mount", follow = true } };
            _player.SetLibrary(_library); Cue(GameplayCueEvent.OnActive);
            var instances = Instances(); Assert.AreEqual(2, instances.Length);
            var world = instances.Single(go => go.transform.parent == null);
            var following = instances.Single(go => go.transform.parent != null);
            Assert.AreEqual(_hero.scene, world.scene);
            _hero.transform.position = Vector3.right * 5;
            Assert.AreEqual(1, world.transform.position.x, .001f); Assert.AreEqual(6, following.transform.position.x, .001f);
            Cue(GameplayCueEvent.Removed); Assert.IsEmpty(Instances());
        }

        [Test]
        public void FiltersOwnerAndOverlappingRegionsHoldUntilLastRemoval()
        {
            Cue(GameplayCueEvent.OnActive, new AbilitySystemComponent()); Assert.IsEmpty(Instances());
            Cue(GameplayCueEvent.OnActive); Cue(GameplayCueEvent.OnActive); Assert.AreEqual(1, Instances().Length);
            Cue(GameplayCueEvent.Removed); Assert.AreEqual(1, Instances().Length);
            Cue(GameplayCueEvent.Removed); Assert.IsEmpty(Instances());
            Cue(GameplayCueEvent.Removed); Assert.IsEmpty(Instances());
        }

        [Test]
        public void ParentTagMappingDoesNotDoubleDispatchAndRebindingCleansWorldInstances()
        {
            _library.bindings = new[] { _library.bindings[0], new CueVfxBinding { cueTag = GameplayTagManager.RequestTag("Test.VFX"), prefab = _prefab } };
            _player.SetLibrary(_library); Assert.AreEqual(2, _router.HandlerCount);
            Cue(GameplayCueEvent.OnActive); Assert.AreEqual(1, Instances().Length);
            Cue(GameplayCueEvent.Removed); Assert.IsEmpty(Instances());
            Cue(GameplayCueEvent.OnActive); _player.Bind(null);
            Assert.IsEmpty(Instances()); Assert.AreEqual(0, _router.HandlerCount);
            _player.Bind(_owner); Assert.AreEqual(2, _router.HandlerCount);
            _player.Bind(_owner); Assert.AreEqual(2, _router.HandlerCount);
        }

        private sealed class Driver : IAnimationDriver
        {
            public float Play(string name, float duration, bool loop) => 1;
            public void Advance(float t) { }
            public void Stop() { }
        }
        [TestCase(true)]
        [TestCase(false)]
        public void AnimationTaskOpensRegionAndEndsVfx(bool cancel)
        {
            var spec = _owner.GrantAbility(new GameplayAbilityDefinition());
            _owner.TryActivateAbility(spec);
            var definition = new SkillAnimationDefinition("Test", 1, false, cueRegions: new[] { new AnimationCueRegion(.2f, .8f, _tag) });
            _owner.StartTask(spec, new AnimationTask(definition, new Driver()));
            _owner.Tick(.19f); Assert.IsEmpty(Instances());
            _owner.Tick(.03f); Assert.AreEqual(1, Instances().Length);
            if (cancel) _owner.EndAbility(spec); else _owner.Tick(.6f);
            Assert.IsEmpty(Instances());
        }

        private SkillAnimationAsset Animation(params (float start, float end)[] ranges)
        {
            var animation = Keep(ScriptableObject.CreateInstance<SkillAnimationAsset>());
            using var so = new SerializedObject(animation);
            so.FindProperty("duration").floatValue = 2;
            var regions = so.FindProperty("cueRegions"); regions.arraySize = ranges.Length;
            for (int i = 0; i < ranges.Length; i++)
            {
                var r = regions.GetArrayElementAtIndex(i);
                r.FindPropertyRelative("start").floatValue = ranges[i].start;
                r.FindPropertyRelative("end").floatValue = ranges[i].end;
                r.FindPropertyRelative("cueTag.m_Name").stringValue = _tag.Name;
            }
            so.ApplyModifiedPropertiesWithoutUndo(); return animation;
        }
        [Test]
        public void PreviewSeekUsesStartPoseForWorldAnchorAndCleansOnRewind()
        {
            var animation = Animation((.2f, .8f));
            using var preview = new CueVfxPreview(_hero);
            void Pose(float t) => _hero.transform.position = Vector3.right * t * 10;
            preview.Sample(animation, .5f, Pose);
            Assert.AreEqual(3, Instances().Single().transform.position.x, .001f);
            Assert.AreEqual(5, _hero.transform.position.x, .001f);
            preview.Sample(animation, .1f, Pose); Assert.IsEmpty(Instances());
            preview.Sample(animation, .7f, Pose); Assert.AreEqual(3, Instances().Single().transform.position.x, .001f);
            preview.Sample(animation, .8f, Pose); Assert.IsEmpty(Instances());
        }

        private void Particles()
        {
            var ps = _prefab.AddComponent<ParticleSystem>();
            var main = ps.main; main.playOnAwake = false; main.startLifetime = 1; main.startSpeed = .2f; main.loop = true;
            var shape = ps.shape; shape.enabled = false;
            var emission = ps.emission; emission.rateOverTime = 20;
        }
        [Test]
        public void PreviewParticlesAreDeterministicAndRetainTailAfterRegion()
        {
            Particles(); _library.bindings[0].stopMode = CueVfxStopMode.StopEmitting; _library.bindings[0].tailSeconds = 1;
            var animation = Animation((.2f, .8f));
            using var preview = new CueVfxPreview(_hero);
            preview.Sample(animation, .6f, _ => { });
            var ps = Instances().Single().GetComponentInChildren<ParticleSystem>();
            var first = new ParticleSystem.Particle[100]; int count = ps.GetParticles(first); Assert.Greater(count, 0);
            preview.Sample(animation, .3f, _ => { }); preview.Sample(animation, .6f, _ => { });
            var second = new ParticleSystem.Particle[100]; Assert.AreEqual(count, ps.GetParticles(second));
            for (int i = 0; i < count; i++) Assert.AreEqual(first[i].position, second[i].position);
            preview.Sample(animation, .9f, _ => { }); Assert.Greater(ps.particleCount, 0);
            preview.Sample(animation, 1.81f, _ => { }); Assert.IsEmpty(Instances());
        }

        [Test]
        public void PreviewMergesOverlapsAndDisposesDetachedEffectsWithScene()
        {
            var animation = Animation((.2f, .7f), (.4f, .9f));
            var render = new PreviewRenderUtility();
            try
            {
                var owner = Object.Instantiate(_hero); render.AddSingleGO(owner);
                using (var preview = new CueVfxPreview(owner))
                {
                    preview.Sample(animation, .5f, _ => { });
                    Assert.AreEqual(1, Instances().Length); Assert.AreEqual(owner.scene, Instances()[0].scene);
                    preview.Sample(animation, .8f, _ => { }); Assert.AreEqual(1, Instances().Length);
                }
                Assert.IsEmpty(Instances());
            }
            finally { render.Cleanup(); }
        }

        [Test]
        public void StopModeCanRetainTailAndUnbindingStillCleansIt()
        {
            Particles(); _library.bindings[0].stopMode = CueVfxStopMode.StopEmitting;
            Cue(GameplayCueEvent.OnActive); Cue(GameplayCueEvent.Removed);
            Assert.AreEqual(1, Instances().Length);
            Assert.IsFalse(Instances()[0].GetComponentInChildren<ParticleSystem>().isEmitting);
            Cue(GameplayCueEvent.OnActive); Assert.AreEqual(2, Instances().Length);
            _player.Bind(null); Assert.IsEmpty(Instances());
        }
    }
}
