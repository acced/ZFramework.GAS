using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class RootMotionCallbackProbe : MonoBehaviour, ISkillMovementAdapter
    {
        public int Callbacks, Applied;
        private void OnAnimatorMove() => Callbacks++;
        public void ApplyRootMotion(Vector3 position, Quaternion rotation) => Applied++;
    }
    public sealed class SkillRootMotionPlaybackTests
    {
        [UnityTest] public IEnumerator SeekEvaluatesRealAnimatorCallbacksWithoutApplyingRootMotion()
        {
#if UNITY_EDITOR
            var go = new GameObject("seek-root-probe"); var root = new GameObject("Root"); root.transform.SetParent(go.transform);
            var animator = go.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var probe = go.AddComponent<RootMotionCallbackProbe>();
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Root", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 2));
            var avatar = AvatarBuilder.BuildGenericAvatar(go, "Root"); animator.avatar = avatar;
            var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "move", clip = clip } }, rootMotion: true);
            try
            {
                driver.Play("move", 1, true); yield return null;
                var graph = (PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                int before = probe.Applied; graph.Evaluate(.02f);
                Assert.Greater(probe.Applied, before, "Control: actual graph evaluation must reach the movement adapter.");
                int callbacks = probe.Callbacks; before = probe.Applied;
                driver.Seek(.6f);
                Assert.Greater(probe.Callbacks, callbacks, "Seek must really invoke OnAnimatorMove; otherwise this test is vacuous.");
                Assert.AreEqual(before, probe.Applied, "Seek must suppress movement within that callback.");
                driver.Stop(); before = probe.Applied; graph.Evaluate(.02f); Assert.AreEqual(before, probe.Applied);
            }
            finally { driver.Dispose(); Object.Destroy(go); Object.Destroy(clip); Object.Destroy(avatar); }
            yield return null;
#else
            yield break;
#endif
        }
    }
}
