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
    public sealed class SkillRunnerTests
    {
        private readonly List<Object> _assets = new();
        private sealed class Driver : IAnimationDriver
        {
            public int Plays, Stops;
            public bool Fail;
            public float Play(string name, float duration, bool loop)
            { Plays++; if (Fail) throw new InvalidOperationException("Driver failed"); return duration > 0 ? duration : 1; }
            public void Advance(float time) { }
            public void Stop() => Stops++;
        }
        [SetUp]
        public void Setup() => TestTags.Initialize();
        [TearDown]
        public void Cleanup() { foreach (var asset in _assets) Object.DestroyImmediate(asset); _assets.Clear(); }
        private T New<T>() where T : ScriptableObject
        { var asset = ScriptableObject.CreateInstance<T>(); _assets.Add(asset); return asset; }
        private GameplayAbilityAsset Skill(bool animated = true, bool loop = false, bool targetEffect = false, bool cooldown = false)
        {
            var asset = New<GameplayAbilityAsset>(); using var ability = new SerializedObject(asset);
            if (animated)
            {
                var animation = New<SkillAnimationAsset>(); using var so = new SerializedObject(animation);
                so.FindProperty("clipName").stringValue = "Test"; so.FindProperty("duration").floatValue = 1;
                so.FindProperty("loop").boolValue = loop;
                var markers = so.FindProperty("markers"); markers.arraySize = 1;
                var marker = markers.GetArrayElementAtIndex(0); marker.FindPropertyRelative("time").floatValue = .5f;
                marker.FindPropertyRelative("key").stringValue = "召唤";
                if (targetEffect)
                {
                    marker.FindPropertyRelative("effect").objectReferenceValue = New<GameplayEffectAsset>();
                    marker.FindPropertyRelative("target").enumValueIndex = (int)AnimationMarkerTarget.Target;
                }
                so.ApplyModifiedPropertiesWithoutUndo(); ability.FindProperty("animation").objectReferenceValue = animation;
            }
            if (cooldown)
            {
                var effect = New<GameplayEffectAsset>(); using var so = new SerializedObject(effect);
                so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration;
                so.FindProperty("duration").floatValue = 2;
                var tags = so.FindProperty("tags.granted"); tags.arraySize = 1;
                tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Test.Cooldown";
                so.ApplyModifiedPropertiesWithoutUndo(); ability.FindProperty("cooldown").objectReferenceValue = effect;
            }
            ability.ApplyModifiedPropertiesWithoutUndo(); return asset;
        }
        [Test]
        public void CastReportsAssetMarkerAndEndsOnCompletion()
        {
            var owner = new AbilitySystemComponent(); var driver = new Driver(); var runner = new SkillRunner(owner, () => driver); var skill = Skill();
            int hits = 0; runner.MarkerReached += (asset, marker) => { Assert.AreSame(skill, asset); Assert.AreEqual("召唤", marker.Key); hits++; };
            Assert.AreEqual(AbilityActivationResult.Activated, runner.TryCast(skill));
            Assert.AreEqual(AbilityActivationResult.AlreadyActive, runner.TryCast(skill)); Assert.AreEqual(1, driver.Plays);
            owner.Tick(.49f); Assert.AreEqual(0, hits); owner.Tick(.02f); Assert.AreEqual(1, hits);
            owner.Tick(.5f); Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(1, driver.Stops);
        }
        [Test]
        public void SharedAssetHasSeparateSpecsAndCooldownPerOwner()
        {
            var skill = Skill(cooldown: true); var a = new AbilitySystemComponent(); var b = new AbilitySystemComponent();
            var da = new Driver(); var db = new Driver(); var ra = new SkillRunner(a, () => da); var rb = new SkillRunner(b, () => db);
            ra.TryCast(skill); Assert.IsFalse(rb.IsActive(skill)); Assert.AreEqual(AbilityActivationResult.Activated, rb.TryCast(skill));
            a.Tick(1); Assert.IsFalse(ra.IsActive(skill)); Assert.IsTrue(rb.IsActive(skill));
            Assert.AreEqual(1, ra.CooldownRemaining(skill), .001f); Assert.AreEqual(2, ra.CooldownDuration(skill));
            Assert.AreEqual(AbilityActivationResult.OnCooldown, ra.TryCast(skill)); Assert.AreEqual(1, da.Plays);
        }
        [Test]
        public void MarkerCanCancelAndLoopDoesNotAutoEnd()
        {
            var owner = new AbilitySystemComponent(); var driver = new Driver(); var runner = new SkillRunner(owner, () => driver); var skill = Skill(loop: true);
            runner.TryCast(skill); owner.Tick(1.2f); Assert.IsTrue(runner.IsActive(skill));
            runner.MarkerReached += (asset, _) => runner.Cancel(asset);
            owner.Tick(.4f); Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(1, driver.Stops);
        }
        [Test]
        public void NoAnimationEndsImmediatelyAndDoesNotRequireDriver()
        {
            var owner = new AbilitySystemComponent(); var runner = new SkillRunner(owner, null); var skill = Skill(false);
            runner.Grant(skill); runner.Grant(skill); Assert.AreEqual(1, owner.GrantedAbilityCount);
            Assert.AreEqual(AbilityActivationResult.Activated, runner.TryCast(skill)); Assert.IsFalse(runner.IsActive(skill));
            Assert.AreEqual(0, owner.ActiveTaskCount);
            owner.Clear(); runner.TryCast(skill); Assert.AreEqual(1, owner.GrantedAbilityCount);
            Assert.IsTrue(runner.Revoke(skill)); Assert.AreEqual(0, owner.GrantedAbilityCount);
        }
        [Test]
        public void InvalidTargetIsRejectedBeforeCooldownAndDriverPlayback()
        {
            var owner = new AbilitySystemComponent(); var driver = new Driver(); var runner = new SkillRunner(owner, () => driver); var skill = Skill(targetEffect: true, cooldown: true);
            Assert.Throws<ArgumentNullException>(() => runner.TryCast(skill));
            Assert.AreEqual(0, driver.Plays); Assert.AreEqual(0, runner.CooldownRemaining(skill)); Assert.IsFalse(runner.IsActive(skill));
        }
        [Test]
        public void DriverFailureDoesNotLeaveAbilityActive()
        {
            var owner = new AbilitySystemComponent(); var driver = new Driver { Fail = true }; var runner = new SkillRunner(owner, () => driver); var skill = Skill();
            Assert.Throws<InvalidOperationException>(() => runner.TryCast(skill));
            Assert.IsFalse(runner.IsActive(skill)); Assert.AreEqual(0, owner.ActiveTaskCount); Assert.AreEqual(1, driver.Stops);
        }
    }
}
