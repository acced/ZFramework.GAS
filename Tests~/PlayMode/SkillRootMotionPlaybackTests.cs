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
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(driver);
                driver.Play("a", 1, true, 1);
                var advanceBlend = typeof(PlayableAnimationDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                advanceBlend.Invoke(driver, new object[] { .5f }); yield return null; yield return null; graph.Evaluate(0);
                Assert.AreEqual(4, bone.localPosition.x, .01f, "The half-weight pose must mix the native controller and the skill.");
                advanceBlend.Invoke(driver, new object[] { .5f }); yield return null; yield return null;
                graph.Evaluate(0); Assert.AreEqual(6, bone.localPosition.x, .01f, "The skill stream must actually override the base pose.");
                var mask=ScriptableObject.CreateInstance<BoneWeightMask>();
                try
                {
                    mask.bones=new[]{new BoneWeightMask.Bone{path="Bone",weight=.25f}};driver.SetNextBoneWeights(mask.Build());
                    driver.Play("a",1,true,0);yield return null; yield return null;
                    Assert.AreEqual(2,bone.localPosition.x,.04f,"Fresh base pose before the manual sample."); graph.Evaluate(0);
                    Assert.AreEqual(3,bone.localPosition.x,.04f,"A quarter-weight bone must retain three quarters of the controller pose.");
                    driver.Play("a",1,true,1);yield return null; yield return null; graph.Evaluate(0);
                    Assert.AreEqual(3,bone.localPosition.x,.04f,"A new full-body skill must fade from the previous weighted layer.");
                    advanceBlend.Invoke(driver,new object[]{.5f});yield return null;yield return null;graph.Evaluate(0);
                    Assert.AreEqual(4.5f,bone.localPosition.x,.04f,"Equal shares of the masked (3) and full (6) poses.");
                    advanceBlend.Invoke(driver,new object[]{.5f});graph.Evaluate(0);
                    Assert.AreEqual(6,bone.localPosition.x,.04f,"Retired bone weights must not constrain the next skill.");
                }
                finally{Object.DestroyImmediate(mask);}
                driver.Stop(); yield return null; yield return null;
                graph.Evaluate(0); Assert.AreEqual(2, bone.localPosition.x, .01f, "Zero skill weight must reveal the continuing base pose.");
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
        [UnityTest] public IEnumerator InertiaReturnsSkillOnlyBonesToRestWithANativeController()
        {
#if UNITY_EDITOR
            string folder=AssetDatabase.GenerateUniqueAssetPath("Assets/GASInertialBase");
            AssetDatabase.CreateFolder("Assets",System.IO.Path.GetFileName(folder));
            var go=new GameObject("controller with skill-only weapon");PlayableAnimationDriver driver=null;
            try{
                Transform Bone(string name,float x){var t=new GameObject(name).transform;t.SetParent(go.transform,false);t.localPosition=Vector3.right*x;return t;}
                var arm=Bone("Arm",1);var weapon=Bone("Weapon",2);
                AnimationClip Clip(string name,float armX,bool drivesWeapon){
                    var clip=new AnimationClip();
                    void Key(string bone,float x)=>AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(bone,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,x));
                    Key("Arm",armX);if(drivesWeapon)Key("Weapon",8);AssetDatabase.CreateAsset(clip,folder+"/"+name+".anim");return clip;}
                var idle=Clip("idle",4,false);var attack=Clip("attack",10,true);var next=Clip("next",6,false);
                var ctrl=UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(folder+"/Base.controller");
                var machine=ctrl.layers[0].stateMachine;machine.AddState("Idle").motion=idle;
                var draw=Clip("draw",4,false);AnimationUtility.SetEditorCurve(draw,
                    EditorCurveBinding.FloatCurve("Weapon",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,5));
                machine.AddState("Draw").motion=draw;
                var anim=go.AddComponent<Animator>();anim.runtimeAnimatorController=ctrl;anim.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                yield return null;Assert.AreEqual(4,arm.localPosition.x,.02f);
                driver=new PlayableAnimationDriver(anim,new[]{new PlayableClipBinding{name="attack",clip=attack},new PlayableClipBinding{name="next",clip=next}}){AutomaticEvaluation=false};
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",flags).GetValue(driver);
                var update=typeof(PlayableAnimationDriver).GetMethod("Update",flags);
                void Step(float dt){anim.Update(dt);update.Invoke(driver,new object[]{dt});graph.Evaluate(0);}
                var blend=new SkillBlend(.4f,mode:SkillBlendMode.Inertialize);
                foreach(bool stop in new[]{false,true}){
                    driver.Play("attack",1,false);Step(0);Assert.AreEqual(8,weapon.localPosition.x,.02f);
                    if(stop)driver.Stop(blend);else driver.Play("next",1,false,blend);
                    Step(0);Assert.AreEqual(8,weapon.localPosition.x,.02f);Step(.2f);
                    Assert.That(weapon.localPosition.x,Is.InRange(2.1f,7.9f),"Exclusive weapon must already be returning at midpoint.");
                    Step(.2f);Assert.AreEqual(2,weapon.localPosition.x,.02f,"final stop="+stop);Step(.05f);Assert.AreEqual(2,weapon.localPosition.x,.02f,"following frame stop="+stop);
                    Assert.AreEqual(stop?4:6,arm.localPosition.x,.04f);
                    if(!stop){
                        anim.Play("Draw",0,0);Step(.05f);Assert.AreEqual(5,weapon.localPosition.x,.02f,"A newly active controller clip can reclaim the released weapon.");
                        anim.Play("Idle",0,0);Step(.05f);
                    }
                }
                driver.AutomaticEvaluation=true;driver.Play("attack",1,false);
                yield return null;yield return null;Assert.AreEqual(8,weapon.localPosition.x,.02f);
                driver.Stop(new SkillBlend(.04f,mode:SkillBlendMode.Inertialize));yield return new WaitForSeconds(.12f);
                yield return null;Assert.AreEqual(2,weapon.localPosition.x,.02f,"The automatic graph must publish its last rest sample before going idle.");
                driver.Play("attack",1,false);yield return null;yield return null;
                Assert.AreEqual(8,weapon.localPosition.x,.02f,"A later cast reconnects to the same controller.");
            }finally{driver?.Dispose();Object.DestroyImmediate(go);AssetDatabase.DeleteAsset(folder);}
#else
            yield break;
#endif
        }
        [UnityTest] public IEnumerator OrdinaryAndInertialStopPublishFinalPoseWithMaskedNativeLayerAndReplacementController()
        {
#if UNITY_EDITOR
            string folder=AssetDatabase.GenerateUniqueAssetPath("Assets/GASMaskedNativeBase");
            AssetDatabase.CreateFolder("Assets",System.IO.Path.GetFileName(folder));
            var go=new GameObject("masked skill stop");PlayableAnimationDriver driver=null;
            try{
                Transform Bone(string n,float x){var t=new GameObject(n).transform;t.SetParent(go.transform,false);t.localPosition=Vector3.right*x;return t;}
                var arm=Bone("Arm",1);var weapon=Bone("Weapon",2);
                AnimationClip Clip(string name,float armX,float? weaponX){
                    var clip=new AnimationClip();
                    void Key(string bone,float x)=>AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(bone,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,x));
                    Key("Arm",armX);if(weaponX.HasValue)Key("Weapon",weaponX.Value);AssetDatabase.CreateAsset(clip,folder+"/"+name+".anim");return clip;
                }
                var idle=Clip("idle",4,null);var overlay=Clip("overlay",5,7);var skill=Clip("skill",10,8);
                var mask=new AvatarMask{transformCount=2};
                mask.SetTransformPath(0,"Arm");mask.SetTransformActive(0,true);mask.SetTransformPath(1,"Weapon");mask.SetTransformActive(1,false);
                AssetDatabase.CreateAsset(mask,folder+"/Arms.mask");
                var ctrl=UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(folder+"/Masked.controller");
                ctrl.layers[0].stateMachine.AddState("Idle").motion=idle;ctrl.AddLayer("Masked overlay");
                var layers=ctrl.layers;layers[1].defaultWeight=1;layers[1].avatarMask=mask;
                var extra=layers[1].stateMachine.AddState("Upper");extra.motion=overlay;extra.writeDefaultValues=false;ctrl.layers=layers;
                var animator=go.AddComponent<Animator>();animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                // The driver is created before the controller is installed.
                driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="skill",clip=skill}});
                animator.runtimeAnimatorController=ctrl;yield return null;yield return null;
                Assert.AreEqual(2,weapon.localPosition.x,.02f);Assert.AreEqual(5,arm.localPosition.x,.02f);
                foreach(var mode in new[]{SkillBlendMode.CrossFade,SkillBlendMode.Inertialize}){
                    foreach(int rate in new[]{1,4}){
                        driver.UpdateRate=new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=rate};
                        driver.Play("skill",1,false);yield return new WaitForSeconds(.08f);
                        Assert.AreEqual(8,weapon.localPosition.x,.03f);
                        driver.Stop(new SkillBlend(.09f,mode:mode));yield return new WaitForSeconds(.18f);
                        for(int j=0;j<5;j++){yield return null;
                            Assert.AreEqual(2,weapon.localPosition.x,.03f,mode+" final weapon at rate "+rate);
                            Assert.AreEqual(5,arm.localPosition.x,.03f,mode+" must leave the native arm intact");}
                    }
                }
                // Write Defaults changes the native stream, not the skill's definition of ownership.
                extra.writeDefaultValues=true;animator.Rebind();animator.Update(0);
                driver.Play("skill",1,false);yield return new WaitForSeconds(.1f);driver.Stop(.05f);
                yield return new WaitForSeconds(.13f);Assert.AreEqual(2,weapon.localPosition.x,.03f);
                Assert.AreEqual(5,arm.localPosition.x,.03f);
                // Simulate an actor outside the camera. Coming back into view must not replay the stale weapon pose.
                driver.Play("skill",1,false);yield return new WaitForSeconds(.1f);
                animator.cullingMode=AnimatorCullingMode.CullCompletely;driver.Stop(.04f);
                yield return new WaitForSeconds(.13f);animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                yield return null;yield return null;Assert.AreEqual(2,weapon.localPosition.x,.03f);
                var other=UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(folder+"/Replacement.controller");
                other.layers[0].stateMachine.AddState("Draw").motion=overlay;
                animator.runtimeAnimatorController=other;yield return null;driver.Play("skill",1,false);yield return null;
                driver.Stop(.05f);yield return new WaitForSeconds(.14f);
                Assert.AreEqual(7,weapon.localPosition.x,.03f,"Replacement controller owns the weapon.");
                animator.runtimeAnimatorController=null;driver.Play("skill",1,false);yield return new WaitForSeconds(.1f);
                Assert.AreEqual(8,weapon.localPosition.x,.03f,"Removing the base controller keeps skill playback available.");
            }finally{driver?.Dispose();Object.DestroyImmediate(go);AssetDatabase.DeleteAsset(folder);}
#else
            yield break;
#endif
        }
        [UnityTest] public IEnumerator InertialSectionRetiresOldClipAndDecaysTheActualBoneOffset()
        {
#if UNITY_EDITOR
            var go=new GameObject("inertial-section");var bone=new GameObject("Bone").transform;bone.SetParent(go.transform);
            bone.localPosition=Vector3.right*1.3f;
            var animator=go.AddComponent<Animator>();var clip=new AnimationClip();
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Bone",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Linear(0,0,1,8));
            using var driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="skill",clip=clip}});
            driver.AutomaticEvaluation=false;
            try
            {
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",flags).GetValue(driver);
                var advance=typeof(PlayableAnimationDriver).GetMethod("Update",flags);
                driver.Play("skill",1,false,.1f);advance.Invoke(driver,new object[]{.1f});driver.Seek(.25f);Assert.AreEqual(2,bone.localPosition.x,.02f);
                Assert.IsTrue(driver.SeekBlend(.75f,new SkillBlend(.4f,mode:SkillBlendMode.Inertialize)));graph.Evaluate(0);
                Assert.AreEqual(2,bone.localPosition.x,.02f);
                Assert.AreEqual(1,driver.CopyWeights(new ClipWeight[4]),"Inertialization must retire the source clip immediately.");
                advance.Invoke(driver,new object[]{.1f});graph.Evaluate(0);
                Assert.AreEqual(2.625f,bone.localPosition.x,.04f,"At a quarter transition the residual is 84.375%, unlike a linear cross-fade.");
                advance.Invoke(driver,new object[]{.3f});graph.Evaluate(0);Assert.AreEqual(6,bone.localPosition.x,.02f);
                driver.Stop(new SkillBlend(.3f,mode:SkillBlendMode.Inertialize));
                advance.Invoke(driver,new object[]{.3f});graph.Evaluate(0);
                Assert.AreEqual(1.3f,bone.localPosition.x,.02f,"Rest is the pose before ordinary playback, not the first inertial transition.");
            }finally{driver.Dispose();Object.Destroy(go);Object.Destroy(clip);}
#endif
            yield return null;
        }
        [UnityTest] public IEnumerator RuntimeOverrideUsesItsOwnBonesAndEmptyMaskFallsBackToCrossFade()
        {
#if UNITY_EDITOR
            var go=new GameObject("dynamic override rig");
            Transform Bone(string name,float rest){var t=new GameObject(name).transform;t.SetParent(go.transform);t.localPosition=Vector3.right*rest;return t;}
            var a=Bone("A",1);var animator=go.AddComponent<Animator>();
            AnimationClip Clip(string path,float value){var c=new AnimationClip();
                AnimationUtility.SetEditorCurve(c,EditorCurveBinding.FloatCurve(path,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,value));return c;}
            var original=Clip("A",7);var replacement=Clip("B",9);var mask=new AvatarMask();
            var driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="skill",clip=original,posePaths=new[]{"Nonexistent"}}}){AutomaticEvaluation=false};
            try{
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",flags).GetValue(driver);
                var update=typeof(PlayableAnimationDriver).GetMethod("Update",flags);
                void Step(float dt){update.Invoke(driver,new object[]{dt});graph.Evaluate(0);}
                driver.Play("skill",1,false);Step(0);Assert.AreEqual(7,a.localPosition.x,.02f);
                var b=Bone("B",2); // The equipment was attached after the driver was created.
                driver.SetNextClip(replacement);driver.Play("skill",1,false,new SkillBlend(.4f,mode:SkillBlendMode.Inertialize));
                Step(0);Assert.AreEqual(7,a.localPosition.x,.02f);Assert.AreEqual(2,b.localPosition.x,.02f);Step(.2f);Assert.That(a.localPosition.x,Is.InRange(1.1f,6.9f));Assert.That(b.localPosition.x,Is.InRange(2.1f,8.9f));
                Step(.2f);Assert.AreEqual(9,b.localPosition.x,.02f);Assert.AreEqual(1,a.localPosition.x,.02f,"completion");Step(.1f);Assert.AreEqual(1,a.localPosition.x,.02f,"next sample");
                mask.AddTransformPath(go.transform,true);for(int i=0;i<mask.transformCount;i++)mask.SetTransformActive(i,false);
                driver.ConfigureNextLayer(mask,false,1);driver.Play("skill",1,false,new SkillBlend(.4f,mode:SkillBlendMode.Inertialize));
                var weights=new ClipWeight[4];Step(.2f);Assert.AreEqual(2,driver.CopyWeights(weights));
                Assert.That(weights[0].Weight,Is.InRange(.1f,.9f),"No masked bones: the previous clip must fade, not disappear.");
                Step(.2f);Assert.AreEqual(1,driver.CopyWeights(weights));
            }finally{driver.Dispose();Object.Destroy(go);Object.Destroy(original);Object.Destroy(replacement);Object.Destroy(mask);}
#endif
            yield return null;
        }
        [UnityTest] public IEnumerator InterruptedInertiaKeepsMaskedAndOutgoingOnlyBonesWhenTheRigGrows()
        {
#if UNITY_EDITOR
            var go=new GameObject("interrupted rig");var animator=go.AddComponent<Animator>();
            Transform Bone(string name,float x){var t=new GameObject(name).transform;t.SetParent(go.transform,false);t.localPosition=Vector3.right*x;return t;}
            var a=Bone("A",1);var b=Bone("B",2);
            AnimationClip Clip(string path,float x){var clip=new AnimationClip();Curve(clip,path,x);return clip;}
            void Curve(AnimationClip clip,string path,float x)=>AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,x));
            var ab=Clip("A",7);Curve(ab,"B",8);var cClip=Clip("C",12);
            var bodyOnly=new AvatarMask();var onlyB=new AvatarMask{transformCount=1};
            onlyB.SetTransformPath(0,"B");onlyB.SetTransformActive(0,true);
            var driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="ab",clip=ab}}){AutomaticEvaluation=false};
            try{
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",flags).GetValue(driver);
                var update=typeof(PlayableAnimationDriver).GetMethod("Update",flags);
                void Step(float dt){update.Invoke(driver,new object[]{dt});graph.Evaluate(0);}
                var blend=new SkillBlend(.4f,mode:SkillBlendMode.Inertialize);
                driver.ConfigureNextLayer(bodyOnly,false,1);driver.Play("ab",1,false,blend);Step(.2f);
                Assert.That(a.localPosition.x,Is.InRange(1.1f,6.9f));Step(.2f);Assert.AreEqual(7,a.localPosition.x,.02f);
                driver.ConfigureNextLayer(onlyB,false,1);driver.Play("ab",1,false,blend);Step(0);
                Assert.AreEqual(7,a.localPosition.x,.02f);Step(.15f);float interrupted=a.localPosition.x;
                Assert.That(interrupted,Is.InRange(1.1f,6.9f),"The outgoing layer still controls A, which is absent from the new mask.");
                var c=Bone("C",3);driver.Prewarm(cClip);Step(0);
                Assert.AreEqual(interrupted,a.localPosition.x,.02f,"Growing the job must preserve its live residual.");
                driver.SetNextClip(cClip);driver.Play("ab",1,false,blend);Step(0);
                Assert.AreEqual(interrupted,a.localPosition.x,.02f);Assert.AreEqual(8,b.localPosition.x,.02f);
                Assert.AreEqual(3,c.localPosition.x,.02f);Step(.4f);Step(.05f);
                Assert.AreEqual(1,a.localPosition.x,.02f);Assert.AreEqual(2,b.localPosition.x,.02f);Assert.AreEqual(12,c.localPosition.x,.02f);
            }finally{driver.Dispose();Object.Destroy(go);Object.Destroy(ab);Object.Destroy(cClip);Object.Destroy(bodyOnly);Object.Destroy(onlyB);}
#endif
            yield return null;
        }
        [UnityTest] public IEnumerator InterruptedEmptyMaskAndZeroBlendKeepTheVisibleResidual()
        {
#if UNITY_EDITOR
            var go=new GameObject("mask in place");Transform Bone(string name,float x){
                var t=new GameObject(name).transform;t.SetParent(go.transform,false);t.localPosition=Vector3.right*x;return t;}
            var a=Bone("A",1);var b=Bone("B",2);var clip=new AnimationClip();
            foreach(var item in new[]{("A",7f),("B",8f)})AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(item.Item1,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,item.Item2));
            var animator=go.AddComponent<Animator>();var mask=new AvatarMask{transformCount=1};
            var driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="hit",clip=clip}}){AutomaticEvaluation=false};
            try{
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",flags).GetValue(driver);
                var update=typeof(PlayableAnimationDriver).GetMethod("Update",flags);
                void Step(float dt){update.Invoke(driver,new object[]{dt});graph.Evaluate(0);}
                var blend=new SkillBlend(.4f,mode:SkillBlendMode.Inertialize);
                mask.SetTransformPath(0,"A");mask.SetTransformActive(0,true);driver.ConfigureNextLayer(mask,false,1);
                driver.Play("hit",1,false,blend);Step(.15f);float midway=a.localPosition.x;
                Assert.That(midway,Is.InRange(1.1f,6.9f));
                mask.SetTransformPath(0,"B");driver.ConfigureNextLayer(mask,false,1);driver.Play("hit",1,false,0);
                Step(0);Assert.AreEqual(midway,a.localPosition.x,.02f);Assert.AreEqual(2,b.localPosition.x,.02f);
                Step(.09f);Assert.Less(a.localPosition.x,midway);Assert.That(b.localPosition.x,Is.InRange(2.1f,7.9f));
                float before=b.localPosition.x;mask.SetTransformActive(0,false);driver.ConfigureNextLayer(mask,false,1);
                driver.Play("hit",1,false,blend);Step(0);Assert.AreEqual(before,b.localPosition.x,.02f);
                Step(.4f);Assert.AreEqual(1,a.localPosition.x,.02f);Assert.AreEqual(2,b.localPosition.x,.02f);
                for(int i=0;i<12;i++){
                    Object.DestroyImmediate(b.gameObject);b=Bone("B",2);driver.Prewarm(clip);
                }
                var rig=typeof(PlayableAnimationDriver).GetField("_poseRig",flags).GetValue(driver);
                var list=(System.Collections.ICollection)rig.GetType().GetField("Bones",flags).GetValue(rig);
                Assert.AreEqual(2,list.Count,"Only live equipment bones should remain registered.");
                var duplicate=Bone("B",11);driver.Prewarm(clip);
                var layout=rig.GetType().GetField("_layout",flags);var key=layout.GetValue(rig);
                for(int i=0;i<3;i++){driver.Play("hit",1,false);Assert.AreSame(key,layout.GetValue(rig),"Stable duplicate siblings must not rebuild the rig every cast.");}
                duplicate.name="Equipment";driver.Prewarm(clip);Assert.AreNotEqual(key,layout.GetValue(rig));
            }finally{driver.Dispose();Object.DestroyImmediate(go);Object.DestroyImmediate(mask);Object.DestroyImmediate(clip);}
#endif
            yield return null;
        }
        [UnityTest] public IEnumerator SectionEntryRetainsInertialBlendAndLeavesAttachmentUnderScriptControl()
        {
#if UNITY_EDITOR
            var go=new GameObject("section-entry");var bone=new GameObject("Bone").transform;bone.SetParent(go.transform);bone.localPosition=Vector3.right*2;
            var fx=new GameObject("script attachment").transform;fx.SetParent(go.transform);
            var animator=go.AddComponent<Animator>();var clip=new AnimationClip();
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Bone",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,6));
            using var driver=new PlayableAnimationDriver(animator,new[]{new PlayableClipBinding{name="skill",clip=clip}});driver.AutomaticEvaluation=false;
            try
            {
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var graph=(PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph",flags).GetValue(driver);
                var update=typeof(PlayableAnimationDriver).GetMethod("Update",flags);var blend=new SkillBlend(.4f,mode:SkillBlendMode.Inertialize);
                foreach(float entry in new[]{0f,.25f}){
                    var asc=new AbilitySystemComponent();var spec=asc.GrantAbility(new GameplayAbilityDefinition());asc.TryActivateAbility(spec);
                    var def=new SkillAnimationDefinition("skill",1,blendInSpec:blend,blendOutSpec:blend,sections:new[]{new AnimationSectionDefinition("first","first",entry,1,null,entry,1)});
                    var task=new AnimationTask(def,driver);asc.StartTask(spec,task);graph.Evaluate(0);
                    Assert.AreEqual(2,bone.localPosition.x,.02f);Assert.AreEqual(entry,task.NormalizedTime,.001f);
                    fx.localPosition=Vector3.right*19;update.Invoke(driver,new object[]{.2f});graph.Evaluate(0);
                    Assert.That(bone.localPosition.x,Is.InRange(2.1f,5.9f));Assert.AreEqual(19,fx.localPosition.x);
                    update.Invoke(driver,new object[]{.2f});graph.Evaluate(0);Assert.AreEqual(6,bone.localPosition.x,.02f);
                    asc.Clear();update.Invoke(driver,new object[]{.4f});graph.Evaluate(0);Assert.AreEqual(2,bone.localPosition.x,.02f);
                }
                for(int i=0;i<200;i++){driver.ConfigureNextLayer(null,(i&1)!=0,1);driver.Play("skill",1,false,.4f);update.Invoke(driver,new object[]{.01f});graph.Evaluate(0);}
                Assert.Less(graph.GetPlayableCount(),40,"Overlapping casts must not retain unbounded layers or clips.");
            }finally{driver.Dispose();Object.Destroy(go);Object.Destroy(clip);}
#endif
            yield return null;
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
            var avatar = AvatarBuilder.BuildGenericAvatar(go, "Root"); animator.avatar = avatar;
            var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "move", clip = clip } }, rootMotion: true);
            try
            {
                driver.Play("move", 1, true); yield return null;
                var graph = (PlayableGraph)typeof(PlayableAnimationDriver).GetField("_graph", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                driver.UpdateRate=new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=4};
                var scheduled=typeof(PlayableAnimationDriver).GetMethod("ScheduledUpdate",BindingFlags.Instance|BindingFlags.NonPublic);
                for(int i=0;i<8;i++){int was=probe.Applied;scheduled.Invoke(driver,new object[]{(long)i,.02f,null});Assert.Greater(probe.Applied,was,"Root motion must reach the adapter on every frame.");}
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
            finally { driver.Dispose(); Object.Destroy(go); Object.Destroy(clip); Object.Destroy(avatar); }
            yield return null;
#else
            yield break;
#endif
        }
    }
}
