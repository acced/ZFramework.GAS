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
        public System.Action OnNative;
        private void OnAnimatorMove(){Callbacks++;OnNative?.Invoke();}
        public System.Action OnApply;
        public void ApplyRootMotion(Vector3 position, Quaternion rotation){Applied++;OnApply?.Invoke();}
    }
    public sealed class SkillRootMotionPlaybackTests
    {
        private sealed class QuarterRate:ISkillAnimationLodPolicy
        {public SkillAnimationRate Rate(Animator _) =>new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=4};}
        [UnityTest] public IEnumerator ControllerHandoffPreservesTriggerAndStateDuringRealPlayback()
            => VerifyHandoff(true);
        [UnityTest] public IEnumerator ControllerHandoffWhileIdleDoesNotEnterDefaultStateAgain()
            => VerifyHandoff(false);
        private static IEnumerator VerifyHandoff(bool triggerHit)
        {
#if UNITY_EDITOR
            const string folder = "Assets/GASRuntimeControllerHandoff"; AssetDatabase.CreateFolder("Assets", "GASRuntimeControllerHandoff");
            GameObject go = null; PlayableAnimationDriver driver = null;
            try
            {
                var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(folder + "/Controller.controller");
                controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger); controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
                var binding = EditorCurveBinding.FloatCurve("Bone", typeof(Transform), "m_LocalPosition.x");
                var clip = new AnimationClip(); AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 2));
                var secondBinding=EditorCurveBinding.FloatCurve("ReplacementBone",typeof(Transform),"m_LocalPosition.x");
                AnimationUtility.SetEditorCurve(clip,secondBinding,AnimationCurve.Constant(0,1,-2));
                AssetDatabase.CreateAsset(clip, folder + "/Clip.anim");
                var skillClip = new AnimationClip(); AnimationUtility.SetEditorCurve(skillClip, binding, AnimationCurve.Constant(0, 1, 6));
                AssetDatabase.CreateAsset(skillClip, folder + "/Skill.anim");
                var idle = controller.layers[0].stateMachine.AddState("Idle"); idle.motion = clip;
                idle.AddStateMachineBehaviour<HandoffEntryProbe>(); HandoffEntryProbe.Entries = 0;
                var hit = controller.layers[0].stateMachine.AddState("Hit"); hit.motion = clip;
                var transition = idle.AddTransition(hit); transition.hasExitTime = false; transition.duration = 0;
                transition.AddCondition(UnityEditor.Animations.AnimatorConditionMode.If, 0, "Fire");
                go = new GameObject("runtime-handoff"); var animator = go.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var bone = new GameObject("Bone").transform; bone.SetParent(go.transform);
                var extra=new GameObject("ReplacementBone").transform;extra.SetParent(go.transform);
                animator.runtimeAnimatorController = controller; yield return null;
                Assert.Greater(HandoffEntryProbe.Entries, 0, "Positive control: the default state must have entered.");
                if (triggerHit) animator.SetTrigger("Fire");
                int idleEntries = HandoffEntryProbe.Entries;
                driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = skillClip } });
                yield return null; yield return null;
                string state = triggerHit ? "Base Layer.Hit" : "Base Layer.Idle";
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName(state));
                Assert.AreEqual(2, bone.localPosition.x, .01f, "The underlying controller must still drive the pose.");
                driver.Play("a", 1, true, 0); yield return null; yield return null;
                Assert.AreEqual(6, bone.localPosition.x, .01f, "The scheduler must publish the real pose after native Animator evaluation.");
                var policy=SkillAnimationManager.Policy;
                try
                {
                    SkillAnimationManager.Policy=new QuarterRate();
                    for(int i=0;i<9;i++)
                    {
                        yield return null;
                        Assert.AreEqual(6,bone.localPosition.x,.01f,"The controller must not flash through the skill between LOD evaluations.");
                    }
                    var replacement=new AnimationClip();AnimationUtility.SetEditorCurve(replacement,secondBinding,AnimationCurve.Constant(0,1,12));
                    AssetDatabase.CreateAsset(replacement,folder+"/Override.anim");driver.SetNextClip(replacement);driver.Play("a",1,true,0);
                    yield return null;yield return null;
                    for(int f=0;f<8;f++){Assert.AreEqual(12,extra.localPosition.x,.01f,"A bone unique to a replacement must be held between LOD samples.");yield return null;}
                }finally{SkillAnimationManager.Policy=policy;}
                driver.Stop(); yield return null; yield return null;
                driver.AutomaticEvaluation = false;
                var sample=typeof(PlayableAnimationDriver).GetMethod("Evaluate",BindingFlags.Instance|BindingFlags.NonPublic);
                void Sample()=>sample.Invoke(driver,new object[]{0f});
                driver.Play("a", 1, true, 1);
                var advanceBlend = typeof(PlayableAnimationDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                advanceBlend.Invoke(driver, new object[] { .5f }); yield return null; yield return null; Sample();
                Assert.AreEqual(4, bone.localPosition.x, .01f, "The half-weight pose must mix the native controller and the skill.");
                advanceBlend.Invoke(driver, new object[] { .5f }); yield return null; yield return null;
                Sample(); Assert.AreEqual(6, bone.localPosition.x, .01f, "The skill stream must actually override the base pose.");
                var mask=ScriptableObject.CreateInstance<BoneWeightMask>();
                try
                {
                    mask.bones=new[]{new BoneWeightMask.Bone{path="Bone",weight=.25f}};driver.SetNextBoneWeights(mask.Build());
                    driver.Play("a",1,true,0);yield return null; yield return null;
                     Sample();
                    Assert.AreEqual(3,bone.localPosition.x,.04f,"A quarter-weight bone must retain three quarters of the controller pose.");
                    driver.Play("a",1,true,1);yield return null; yield return null; Sample();
                    Assert.AreEqual(3,bone.localPosition.x,.04f,"A new full-body skill must fade from the previous weighted layer.");
                    advanceBlend.Invoke(driver,new object[]{.5f});yield return null;yield return null;Sample();
                    Assert.AreEqual(4.5f,bone.localPosition.x,.04f,"Equal shares of the masked (3) and full (6) poses.");
                    advanceBlend.Invoke(driver,new object[]{.5f});Sample();
                    Assert.AreEqual(6,bone.localPosition.x,.04f,"Retired bone weights must not constrain the next skill.");
                }
                finally{Object.DestroyImmediate(mask);}
                driver.Stop(); yield return null; yield return null;
                Sample(); Assert.AreEqual(2, bone.localPosition.x, .01f, "Zero skill weight must reveal the continuing base pose.");
                animator.SetFloat("Speed", .7f); float before = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                driver.Dispose();
                for (int frame = 0; frame < 4; frame++) yield return null;
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName(state));
                Assert.Greater(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, before, "The restored controller must keep advancing in subsequent frames.");
                Assert.AreEqual(.7f, animator.GetFloat("Speed"));
                Assert.AreEqual(idleEntries, HandoffEntryProbe.Entries, "Handoff must not generate a spurious default state entry.");
            }
            finally { driver?.Dispose(); if (go != null) Object.DestroyImmediate(go); AssetDatabase.DeleteAsset(folder); }
#else
            yield break;
#endif
        }
        private sealed class FailingPolicy:ISkillAnimationLodPolicy
        {
            internal Animator Fault;
            public SkillAnimationRate Rate(Animator animator) => animator==Fault ? throw new System.InvalidOperationException("LOD probe") : default;
        }
        [UnityTest] public IEnumerator SecondsRateStaggersInstancesAndOneFailingPolicyDoesNotStopItsNeighbours()
        {
#if UNITY_EDITOR
            var parent=new GameObject("LOD isolation");var clip=new AnimationClip();
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Bone",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,7));
            var drivers=new PlayableAnimationDriver[8];var animators=new Animator[8];var old=SkillAnimationManager.Policy;
            try{
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var scheduled=typeof(PlayableAnimationDriver).GetMethod("ScheduledUpdate",flags);
                var step=(System.Action<PlayableAnimationDriver,long,float,ISkillAnimationLodPolicy>)System.Delegate.CreateDelegate(typeof(System.Action<PlayableAnimationDriver,long,float,ISkillAnimationLodPolicy>),scheduled);
                for(int i=0;i<8;i++){
                    var go=new GameObject("actor");go.transform.SetParent(parent.transform);new GameObject("Bone").transform.SetParent(go.transform);
                    animators[i]=go.AddComponent<Animator>();
                    drivers[i]=new PlayableAnimationDriver(animators[i],new[]{new PlayableClipBinding{name="clip",clip=clip}},updateRate:new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNSeconds,seconds=.08f});
                    drivers[i].Play("clip",1,true);
                }
                foreach(var driver in drivers)step(driver,-1,0,null); // Publish each new clip once before testing steady-state staggering.
                for(int f=0;f<8;f++){
                    long before=SkillAnimationManager.EvaluationCount;foreach(var driver in drivers)step(driver,f,.02f,null);
                    Assert.That(SkillAnimationManager.EvaluationCount-before,Is.InRange(1L,4L));
                }
                SkillAnimationManager.Policy=new FailingPolicy{Fault=animators[0]};
                var manager=Object.FindFirstObjectByType<SkillAnimationManager>();Assert.IsNotNull(manager);
                foreach(var a in animators)a.transform.Find("Bone").localPosition=Vector3.zero;
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("LOD probe"));
                typeof(SkillAnimationManager).GetMethod("LateUpdate",flags).Invoke(manager,null);
                Assert.AreEqual(7,animators[7].transform.Find("Bone").localPosition.x,.01f);
            }finally{SkillAnimationManager.Policy=old;foreach(var driver in drivers)driver?.Dispose();Object.Destroy(parent);Object.Destroy(clip);}
#endif
            yield return null;
        }
        [UnityTest] public IEnumerator SectionJumpBlendsPoseWithoutAdvancingSkippedGameplay()
        {
#if UNITY_EDITOR
            var go = new GameObject("section-pose"); var bone = new GameObject("Bone").transform; bone.SetParent(go.transform);
            var animator = go.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Bone", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 10));
            using var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = clip } });
            try
            {
                var graph = (PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(driver);
                var asc = new AbilitySystemComponent(); var spec = asc.GrantAbility(new GameplayAbilityDefinition()); asc.TryActivateAbility(spec);
                int hits = 0;
                var animation = new SkillAnimationDefinition("a", 1, markers:new[] { new AnimationMarker(.4f, "skip") },
                    sections:new[] { new AnimationSectionDefinition("a","A",0,.25f,null,0,.25f),
                    new AnimationSectionDefinition("b","B",.75f,1,null,.75f,1,new SkillBlend(.2f, SkillBlendCurve.QuadIn, SkillBlendMode.Freeze)) });
                var task = new AnimationTask(animation, driver, onMarker: (_, __) => hits++); asc.StartTask(spec, task);
                driver.Seek(.2f); graph.Evaluate(0); float before = bone.localPosition.x;
                Assert.That(before, Is.EqualTo(2).Within(.02f));
                Assert.IsTrue(task.JumpToSection("b")); graph.Evaluate(0);
                Assert.That(bone.localPosition.x, Is.EqualTo(before).Within(.02f));
                typeof(PlayableAnimationDriver).GetMethod("Update", BindingFlags.Instance|BindingFlags.NonPublic).Invoke(driver,new object[]{.1f});
                graph.Evaluate(0); Assert.That(bone.localPosition.x, Is.EqualTo(Mathf.Lerp(2,7.5f,.25f)).Within(.03f));
                Assert.AreEqual(0,hits); asc.Clear();
            }
            finally { driver.Dispose(); Object.Destroy(go); Object.Destroy(clip); }
            yield return null;
#else
            yield break;
#endif
        }
        [UnityTest] public IEnumerator WeightedPoseWithoutControllerKeepsTheOriginalRestTransform()
        {
#if UNITY_EDITOR
            var go=new GameObject("weighted-rest");var bone=new GameObject("Bone").transform;bone.SetParent(go.transform);bone.localPosition=Vector3.right*2;
            var animator=go.AddComponent<Animator>();var clip=new AnimationClip();
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Bone",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,6));
            using var driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="skill",clip=clip}});
            var mask=ScriptableObject.CreateInstance<BoneWeightMask>();
            try
            {
                mask.bones=new[]{new BoneWeightMask.Bone{path="Bone",weight=.25f}};
                driver.SetNextBoneWeights(mask.Build());driver.Play("skill",1,true);
                for(int f=0;f<5;f++){yield return null;if(f>0)Assert.AreEqual(3,bone.localPosition.x,.04f);}
            }finally{driver.Dispose();Object.Destroy(go);Object.Destroy(clip);Object.Destroy(mask);}
#else
            yield break;
#endif
        }
        [UnityTest] public IEnumerator SeekEvaluatesRealAnimatorCallbacksWithoutApplyingRootMotion()
        {
#if UNITY_EDITOR
            var go = new GameObject("seek-root-probe"); var root = new GameObject("Root"); root.transform.SetParent(go.transform);
            var animator = go.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var probe = go.AddComponent<RootMotionCallbackProbe>();
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Root", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 2));
            const string controllerPath = "Assets/GASRootMotionProbe.controller";
            var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            var idle = controller.layers[0].stateMachine.AddState("Idle"); idle.motion = clip;
            animator.runtimeAnimatorController = controller;
            var avatar = AvatarBuilder.BuildGenericAvatar(go, "Root"); animator.avatar = avatar;
            var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "move", clip = clip } }, rootMotion: true);
            try
            {
                driver.Play("move", 1, true); yield return null;
                var graph = (PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                driver.UpdateRate=new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=4};
                var scheduled=typeof(PlayableAnimationDriver).GetMethod("ScheduledUpdate",BindingFlags.Instance|BindingFlags.NonPublic);
                for(int i=0;i<8;i++){int was=probe.Applied;scheduled.Invoke(driver,new object[]{(long)i,.02f,null});Assert.AreEqual(was + 1, probe.Applied,"The controller and skill together must forward exactly one delta per sample.");}
                int before = probe.Applied; graph.Evaluate(.02f);
                Assert.Greater(probe.Applied, before, "Control: actual graph evaluation must reach the movement adapter.");
                int callbacks = probe.Callbacks; before = probe.Applied;
                driver.Seek(.6f);
                Assert.Greater(probe.Callbacks, callbacks, "Seek must really invoke OnAnimatorMove; otherwise this test is vacuous.");
                Assert.AreEqual(before, probe.Applied, "Seek must suppress movement within that callback.");
                graph.Evaluate(.02f);
                Assert.Greater(probe.Applied, before, "Positive control immediately before Stop must apply root motion again.");
                driver.Stop(.2f); before = probe.Applied; callbacks = probe.Callbacks; graph.Evaluate(.02f);
                Assert.Greater(probe.Callbacks, callbacks, "The fading graph still evaluates OnAnimatorMove after Stop.");
                Assert.AreEqual(before, probe.Applied, "Stop must disconnect the movement adapter immediately.");
            }
            finally { driver.Dispose(); Object.Destroy(go); Object.Destroy(clip); Object.Destroy(avatar); AssetDatabase.DeleteAsset(controllerPath); }
            yield return null;
#else
            yield break;
#endif
        }
    }
}
