using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class AnimationClipEditorTests
    {
        private sealed class TestClip : ScriptableObject
        {
            public float duration = 2;
            public bool failPreview;
        }

        private sealed class TestClipEditor : IAnimationClipEditor
        {
            public Type AssetType => typeof(TestClip);
            public readonly List<TestSampler> Samplers = new();
            public int Creates;
            public float Duration(Object clip) => ((TestClip)clip).duration;
            public IAnimationPreviewSampler CreatePreview(GameObject instance, Object clip)
            {
                Creates++;
                if (((TestClip)clip).failPreview) throw new InvalidOperationException("Preview rig mismatch");
                var sampler = new TestSampler(); Samplers.Add(sampler); return sampler;
            }
        }

        private sealed class TestSampler : IAnimationPreviewSampler
        {
            public float Time;
            public int Disposals;
            public void Sample(float normalizedTime) => Time = normalizedTime;
            public void Dispose() => Disposals++;
        }

        private TestClipEditor _editor;
        private readonly List<Object> _objects = new();
        private GameplayAbilityAsset _ability;
        private SkillAnimationAsset _animation;
        private SkillDocument _doc;
        private PreviewPanel _preview;

        [OneTimeSetUp]
        public void RegisterProvider()
        {
            var probe = ScriptableObject.CreateInstance<TestClip>();
            try
            {
                if (AnimationClipEditors.TryGet(probe, out var editor)) _editor = (TestClipEditor)editor;
                else { _editor = new TestClipEditor(); AnimationClipEditors.Register(_editor); }
            }
            finally { Object.DestroyImmediate(probe); }
        }

        [SetUp]
        public void SetUp()
        {
            _editor.Samplers.Clear(); _editor.Creates = 0;
            _ability = Asset<GameplayAbilityAsset>(); _animation = Asset<SkillAnimationAsset>();
            using (var so = new SerializedObject(_ability))
            { so.FindProperty("animation").objectReferenceValue = _animation; so.ApplyModifiedPropertiesWithoutUndo(); }
            _doc = new SkillDocument(); _doc.Open(_ability, false); Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            _preview?.Dispose(); _preview = null; _doc.Dispose(); Undo.ClearAll();
            foreach (var asset in _objects) Object.DestroyImmediate(asset);
            _objects.Clear();
        }

        private T Asset<T>() where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>(); _objects.Add(asset); return asset;
        }

        private void Bind(Object clip)
        {
            using var so = new SerializedObject(_animation);
            so.FindProperty("clip").objectReferenceValue = clip;
            so.ApplyModifiedPropertiesWithoutUndo(); _doc.Notify(new ChangeSet(_animation, true));
        }

        [Test]
        public void UnityClipPreviewSupportsBackwardSeekAndRestoresOriginalTransform()
        {
            var clip = new AnimationClip(); var instance = new GameObject("Pose");
            _objects.Add(clip); _objects.Add(instance);
            instance.transform.localPosition = new Vector3(7, 8, 9);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Linear(0, 0, 2, 2));
            Assert.IsTrue(AnimationClipEditors.TryGet(clip, out var editor));
            Assert.AreEqual(2, editor.Duration(clip));
            using (var sampler = editor.CreatePreview(instance, clip))
            {
                sampler.Sample(.75f); Assert.AreEqual(1.5f, instance.transform.localPosition.x, .0001f);
                sampler.Sample(.25f); Assert.AreEqual(.5f, instance.transform.localPosition.x, .0001f);
                Assert.AreEqual(8, instance.transform.localPosition.y);
            }
            Assert.AreEqual(new Vector3(7, 8, 9), instance.transform.localPosition);
        }

        [Test]
        public void ExtensionClipSelectionRetimesTimelineAndUndoRestoresIt()
        {
            SkillTimelineAuthoring.Edit(_animation, writer =>
            {
                writer.Clip(null, 1); writer.Marker("hit", .25f, "Hit");
                writer.TagWindow("window", .2f, -1);
            });
            _doc.Notify(new ChangeSet(_animation, true));
            using var timeline = new TimelinePanel(_doc); timeline.Rebuild(new ChangeSet(_animation, true));
            var clip = Asset<TestClip>();
            timeline.Query<ObjectField>().ToList().Single(field => field.label == "动画片段").value = clip;
            Assert.AreSame(clip, _animation.Clip); Assert.AreEqual(2, _animation.Duration);
            Assert.AreEqual(.5f, _animation.Definition.Markers[0].Time, .0001f);
            Assert.AreEqual(.4f, _animation.Definition.TagWindows[0].Start, .0001f);
            Assert.AreEqual(-1, _animation.Definition.TagWindows[0].End);
            Assert.AreEqual(.24f, _animation.BlendIn, .0001f);
            Undo.PerformUndo();
            Assert.IsNull(_animation.Clip); Assert.AreEqual(1, _animation.Duration);
            Assert.AreEqual(.25f, _animation.Definition.Markers[0].Time, .0001f);
        }

        [Test]
        public void ResourceValidationAllowsEmptyAndStaticClipsButRejectsUnknownAndInvalidSources()
        {
            using var validation = new ValidationService(_doc);
            void Check(Object clip, bool valid)
            {
                Bind(clip); validation.ValidateNow(strict: true);
                var errors = validation.Issues.Where(issue => issue.Id == "GAS015").ToArray();
                Assert.AreEqual(valid ? 0 : 1, errors.Length);
                if (!valid) Assert.AreEqual("clip", errors[0].PropertyPath);
            }
            Check(null, true);
            var unityClip = new AnimationClip(); _objects.Add(unityClip); Check(unityClip, true);
            var texture = new Texture2D(1, 1); _objects.Add(texture); Check(texture, false);
            var custom = Asset<TestClip>(); Check(custom, true);
            custom.duration = float.NaN; Check(custom, false);
            custom.duration = -1; Check(custom, false);
            custom.duration = 0; Check(custom, true);
        }

        [Test]
        public void PreviewReusesSamplerWhileSeekingAndReleasesItWhenResourceChanges()
        {
            var first = Asset<TestClip>(); Bind(first);
            _preview = new PreviewPanel(_doc); _preview.Load();
            var sampler = _editor.Samplers.Single();
            _doc.Playback.Seek(.75f, 1); _doc.Playback.Seek(.25f, 1);
            Assert.AreEqual(.25f, sampler.Time); Assert.AreEqual(1, _editor.Creates);
            Bind(Asset<TestClip>());
            Assert.AreEqual(1, sampler.Disposals); Assert.AreEqual(2, _editor.Creates);
            var replacement = _editor.Samplers.Last(); Bind(null);
            Assert.AreEqual(1, replacement.Disposals); Assert.AreEqual(2, _editor.Creates);
        }

        [Test]
        public void PreviewCreationErrorDoesNotRepeatOnSeekAndCanBeCorrected()
        {
            var broken = Asset<TestClip>(); broken.failPreview = true; Bind(broken);
            _preview = new PreviewPanel(_doc);
            Assert.DoesNotThrow(_preview.Load);
            Assert.IsTrue(_preview.Query<Label>().ToList().Any(label => label.text == "Preview rig mismatch"));
            _doc.Playback.Seek(.5f, 1); Assert.AreEqual(1, _editor.Creates);
            Bind(Asset<TestClip>());
            Assert.AreEqual(2, _editor.Creates);
            Assert.IsFalse(_preview.Query<Label>().ToList().Any(label => label.text == "Preview rig mismatch"));
            Assert.AreEqual(.5f, _editor.Samplers.Single().Time);
        }
    }
}
