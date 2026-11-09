using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
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
            var first = new AudioSkillTrack { clip = clip, end = 5, loop = true };
            var second = new AudioSkillTrack { clip = clip, end = 5, loop = true };
            animation.runtimeTracks = new SkillRuntimeTrack[] { first, second };
            using var audio = new SkillPreviewAudio();
            try
            {
                audio.Sample(animation, 0, true, 0);
                var a=audio.Get(first.Id).Source;var b=audio.Get(second.Id).Source;
                // Install the taps first, then wait for observed mixer output instead of assuming
                // editor frame time is the time the audio device has actually been running.
                var pcm=new float[1024];
                bool HasSignal(AudioSource source){
                    source.GetOutputData(pcm,0);
                    return source.isPlaying && Array.Exists(pcm,s=>Mathf.Abs(s)>.001f);
                }
                HasSignal(a);HasSignal(b);
                double until=EditorApplication.timeSinceStartup+4; bool simultaneous=false;
                while(!simultaneous && EditorApplication.timeSinceStartup<until){
                    float time=(float)(EditorApplication.timeSinceStartup-until+4);
                    audio.Sample(animation,time,true,0);
                    simultaneous=HasSignal(a) && HasSignal(b);
                    if(!simultaneous)yield return null;
                }
                Assert.IsTrue(simultaneous,"Both overlapping sources must be playing and supply actual PCM.");
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
        [Test] public void WindowClockAndTimelinePauseKeepPreviewAudioSynchronized()
        {
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/GASPreviewClockTest.asset");
            var skill = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); AssetDatabase.CreateAsset(skill, path);
            var animation = ScriptableObject.CreateInstance<SkillAnimationAsset>(); AssetDatabase.AddObjectToAsset(animation, skill);
            var clip = AudioClip.Create("preview-clock", 48000 * 3, 1, 48000, false);
            var track = new AudioSkillTrack { clip = clip, end = 3, loop = true }; animation.runtimeTracks = new SkillRuntimeTrack[] { track };
            using (var so = new SerializedObject(animation)) { so.FindProperty("clipName").stringValue = "a"; so.FindProperty("duration").floatValue = 3; so.ApplyModifiedPropertiesWithoutUndo(); }
            using (var so = new SerializedObject(skill)) { so.FindProperty("animation").objectReferenceValue = animation; so.ApplyModifiedPropertiesWithoutUndo(); }
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/GASPreviewClockTest.prefab");
            var motion = new AnimationClip { name = "a", legacy = true };
            motion.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 3, 1));
            AssetDatabase.AddObjectToAsset(motion, skill);
            var source = new GameObject("preview-clock-source"); source.AddComponent<Animation>().AddClip(motion, "a");
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath); Object.DestroyImmediate(source);
            SkillEditorWindow window = null;
            try
            {
                SkillIndex.Invalidate(); window = SkillEditorWindow.Open(); window.Show(skill);
                window.Document.PreviewPrefab = prefab;
                var preview = window.rootVisualElement.Q<PreviewPanel>(); preview.Load();
                Assert.IsFalse(preview.Query<Toggle>().ToList().Any(t=>t.label=="连锁路线" || t.label=="范围"));
                var targetTools=preview.Query<Foldout>().ToList().Single(x=>x.text=="目标预演");
                Assert.IsFalse(targetTools.value);
                var field=typeof(PreviewPanel).GetField("_simulation",BindingFlags.Instance|BindingFlags.NonPublic);
                Assert.IsNull(field.GetValue(preview));targetTools.value=true;Assert.NotNull(field.GetValue(preview));
                targetTools.value=false;Assert.IsNull(field.GetValue(preview));
                var motionSwitch=preview.Query<Toggle>().ToList().Single(t=>t.label=="根位移");
                preview.Query<Foldout>().ToList().Single(x=>x.text=="动画辅助").value=true;
                Assert.AreEqual(DisplayStyle.Flex,motionSwitch.style.display.value,"The preview clip moves its root.");
                var sound = preview.Query<Toggle>().ToList().Single(t => t.label == "音效");
                var previewTools = typeof(PreviewPanel).GetField("_tools", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(preview);
                Assert.IsNotNull(previewTools, "The actual preview must load its sampler and tools.");
                var audio = (SkillPreviewAudio)typeof(SkillPreviewTools).GetField("_audio", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(previewTools);
                var playback = window.Document.Playback;
                var timeline = window.rootVisualElement.Q<TimelinePanel>();
                window.AdvancePlayback(100); timeline.TogglePlay();
                Assert.IsTrue(playback.Playing); Assert.AreEqual(0, audio.Count, "No source should be created while muted.");
                sound.value = true; Assert.AreEqual(1, audio.Count);
                var channel = audio.Get(track.Id); int seek = playback.SeekVersion;
                window.AdvancePlayback(100.6);
                Assert.AreEqual(.6f, playback.Time, .0001f, "Use the window's real clock path; a stalled frame must not be clamped to .1 seconds.");
                Assert.AreEqual(seek, playback.SeekVersion); Assert.AreEqual(1, channel.SeekCount);
                timeline.TogglePlay(); Assert.AreEqual(0, audio.Count, "Pause must stop audio immediately without waiting for another Tick or Seek.");
                window.AdvancePlayback(102); Assert.AreEqual(.6f, playback.Time, .0001f);
                timeline.TogglePlay(); Assert.AreEqual(1, audio.Count); Assert.AreEqual(.6f, audio.Get(track.Id).Source.time, .05f);
                window.AdvancePlayback(102.25); Assert.AreEqual(.85f, playback.Time, .0001f);
                var playingSource = audio.Get(track.Id).Source; sound.value = false;
                window.AdvancePlayback(102.5); Assert.AreEqual(1.1f, playback.Time, .0001f);
                Assert.IsTrue(playingSource == null); Assert.AreEqual(0, audio.Count, "Mute releases the active source and stays silent while the timeline advances.");
            }
            finally { window?.Close(); AssetDatabase.DeleteAsset(prefabPath); AssetDatabase.DeleteAsset(path); Object.DestroyImmediate(clip); SkillIndex.Invalidate(); }
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
