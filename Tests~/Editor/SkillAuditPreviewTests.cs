using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SkillAuditPreviewTests
    {
        [UnityTest] public IEnumerator PreviewChannelsProduceAudioSamplesInEditMode()
        {
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/GASAudioAuditionTest.wav");
            using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                int count = 48000 * 2;
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
                for (int i = 0; i < count; i++) writer.Write((short)(Mathf.Sin(i * 440 * 2 * Mathf.PI / 48000) * 3200));
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path); clip.LoadAudioData();
            while (clip.loadState == AudioDataLoadState.Loading) yield return null;
            var animation = ScriptableObject.CreateInstance<SkillAnimationAsset>();
            var first = new AudioSkillTrack { clip = clip, end = 2, loop = true };
            var second = new AudioSkillTrack { clip = clip, end = 2, loop = true };
            animation.runtimeTracks = new SkillRuntimeTrack[] { first, second };
            using var audio = new SkillPreviewAudio();
            try
            {
                double start = EditorApplication.timeSinceStartup;
                audio.Sample(animation, 0, true, 0);
                // GetOutputData installs the DSP capture tap on its first call. Wait for
                // audio buffers after installing it before treating zero samples as silence.
                foreach (var track in new[] { first, second }) audio.Get(track.Id).Source.GetOutputData(new float[1024], 0);
                while (EditorApplication.timeSinceStartup - start < .5)
                {
                    audio.Sample(animation, (float)(EditorApplication.timeSinceStartup - start), true, 0);
                    yield return null;
                }
                foreach (var track in new[] { first, second })
                {
                    var source = audio.Get(track.Id).Source;
                    Assert.IsTrue(source.isPlaying, "Each independent audition source must play.");
                    var output = new float[1024]; source.GetOutputData(output, 0);
                    float peak = 0; foreach (var sample in output) peak = Mathf.Max(peak, Mathf.Abs(sample));
                    Assert.Greater(peak, .001f, "Each overlapping channel must produce actual PCM samples.");
                }
            }
            finally { audio.Dispose(); Object.DestroyImmediate(animation); AssetDatabase.DeleteAsset(path); }
        }
        [Test] public void OverlappingAudioChannelsAreIndependentAndSlowFramesDoNotSeek()
        {
            var clip = AudioClip.Create("preview-test", 48000 * 3, 1, 48000, false);
            var animation = ScriptableObject.CreateInstance<SkillAnimationAsset>();
            var first = new AudioSkillTrack { start = 0, end = 1, clip = clip, volume = .3f };
            var second = new AudioSkillTrack { start = .1f, end = 2, clip = clip, volume = .7f };
            animation.runtimeTracks = new SkillRuntimeTrack[] { first, second };
            using var audio = new SkillPreviewAudio(); using var otherWindow = new SkillPreviewAudio();
            try
            {
                var playback = new PlaybackModel { Playing = true, Loop = false };
                playback.Seek(.2f, 3); audio.Sample(animation, playback.Time, true, playback.SeekVersion);
                Assert.AreEqual(2, audio.Count);
                var a = audio.Get(first.Id); var b = audio.Get(second.Id);
                Assert.AreNotSame(a.Source, b.Source);
                Assert.AreEqual(.3f, a.Source.volume); Assert.AreEqual(.7f, b.Source.volume);
                Assert.AreEqual(.2, a.Source.time, .05); Assert.AreEqual(.1, b.Source.time, .05);
                int seek = playback.SeekVersion;
                playback.Tick(.6f, 3); audio.Sample(animation, playback.Time, true, playback.SeekVersion);
                Assert.AreEqual(seek, playback.SeekVersion); Assert.AreEqual(1, a.SeekCount); Assert.AreEqual(1, b.SeekCount);
                Assert.AreSame(a, audio.Get(first.Id)); Assert.AreSame(b, audio.Get(second.Id));
                playback.Seek(.4f, 3); audio.Sample(animation, playback.Time, true, playback.SeekVersion);
                Assert.AreEqual(2, a.SeekCount); Assert.AreEqual(.3, b.Source.time, .05);
                otherWindow.Sample(animation, .5f, true, 0);
                playback.Tick(.7f, 3); audio.Sample(animation, playback.Time, true, playback.SeekVersion);
                Assert.AreEqual(1, audio.Count); Assert.IsTrue(a.Source == null); Assert.IsTrue(b.Source != null);
                audio.Sample(animation, playback.Time, false, playback.SeekVersion);
                Assert.AreEqual(0, audio.Count); Assert.AreEqual(2, otherWindow.Count); Assert.IsTrue(otherWindow.Get(first.Id).Source != null);
            }
            finally { audio.Dispose(); otherWindow.Dispose(); Object.DestroyImmediate(animation); Object.DestroyImmediate(clip); }
        }
        [TestCase(false), TestCase(true)] public void DestroyedAnimatorAndLegacyDriversAreRecreated(bool legacy)
        {
            var go = new GameObject("driver-lifetime-test"); var player = go.AddComponent<SkillAnimator>();
            var controller = new UnityEditor.Animations.AnimatorController(); controller.AddLayer("Base");
            try
            {
                Component component = legacy ? go.AddComponent<Animation>() : go.AddComponent<Animator>();
                if (component is Animator animator) animator.runtimeAnimatorController = controller;
                player.SetBackend(legacy ? (AnimationBackend)new LegacyAnimationBackend() : new AnimatorBackend());
                var first = player.Driver; Assert.IsTrue(((IAnimationDriverLifetime)first).IsValid);
                Object.DestroyImmediate(component); Assert.IsFalse(((IAnimationDriverLifetime)first).IsValid);
                if (legacy) go.AddComponent<Animation>(); else go.AddComponent<Animator>().runtimeAnimatorController = controller;
                Assert.AreNotSame(first, player.Driver);
            }
            finally
            {
                player.SetBackend(null); Object.DestroyImmediate(go);
                foreach (var layer in controller.layers) Object.DestroyImmediate(layer.stateMachine);
                Object.DestroyImmediate(controller);
            }
        }
    }
}
