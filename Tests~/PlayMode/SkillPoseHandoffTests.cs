using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif
using Object=UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class SkillPoseHandoffTests
    {
#if UNITY_EDITOR
        private sealed class Fixture : IDisposable
        {
            public readonly string Folder;
            public readonly GameObject Go=new("Handoff actor");
            public readonly Transform Arm,Weapon;
            public readonly Animator Anim;
            public readonly SkillAnimator Skills;
            public readonly PlayableAnimationBackend Backend;
            public readonly AnimationClip WeaponClip;
            public readonly AnimatorController Controller;
            public Fixture(bool writeDefaults,bool previouslyBound=false){
                Folder=AssetDatabase.GenerateUniqueAssetPath("Assets/GASOwnershipTest");
                AssetDatabase.CreateFolder("Assets",System.IO.Path.GetFileName(Folder));
                Arm=Bone("Arm",1);Weapon=Bone("Weapon",2);
                var idle=Clip("Idle","Arm",4);WeaponClip=Clip("Skill","Weapon",8);
                Controller=AnimatorController.CreateAnimatorControllerAtPath(Folder+"/Native.controller");
                var machine=Controller.layers[0].stateMachine;
                var initial=machine.AddState("Idle");initial.motion=idle;initial.writeDefaultValues=writeDefaults;
                if(previouslyBound){var draw=machine.AddState("Draw");draw.motion=Clip("Draw","Weapon",5);draw.writeDefaultValues=writeDefaults;}
                else foreach(var clip in Controller.animationClips)foreach(var curve in AnimationUtility.GetCurveBindings(clip))
                    Assert.AreNotEqual("Weapon",curve.path,"the native controller must have no weapon binding at all");
                Anim=Go.AddComponent<Animator>();Anim.runtimeAnimatorController=Controller;Anim.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                Skills=Go.AddComponent<SkillAnimator>();Backend=new PlayableAnimationBackend{clips=new[]{new PlayableClipBinding{name="skill",clip=WeaponClip}}};
                Skills.SetBackend(Backend);
            }
            public Transform Bone(string name,float x){var t=new GameObject(name).transform;t.SetParent(Go.transform,false);t.localPosition=Vector3.right*x;return t;}
            public AnimationClip Clip(string name,string bone,float x){
                var clip=new AnimationClip();AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(bone,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,x));
                AssetDatabase.CreateAsset(clip,Folder+"/"+name+".anim");return clip;
            }
            public void Dispose(){Object.DestroyImmediate(Go);AssetDatabase.DeleteAsset(Folder);}
        }
        private const System.Reflection.BindingFlags Private=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        private static void Step(PlayableAnimationDriver d,float delta){
            typeof(PlayableAnimationDriver).GetMethod("ScheduledUpdate",Private).Invoke(d,new object[]{(long)Time.frameCount,delta,null});
        }
        [UnityTest] public IEnumerator DisabledHostRejectsNewCastsAndOnlyAnAuthoredExitFinishes(){
            using var f=new Fixture(false);yield return null;
            var cache=typeof(SkillAnimator).GetField("_driver",Private);
            Assert.IsNull(cache.GetValue(f.Skills),"Spawning an unused host must not create a graph");
            var d=(PlayableAnimationDriver)f.Skills.Driver;new AnimationTask(new SkillAnimationDefinition("skill",1,blendOut:.25f),d);
            yield return null;yield return null;Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            f.Skills.enabled=false;
            Assert.Throws<InvalidOperationException>(()=>{var ignored=f.Skills.Driver;});f.Skills.Prewarm();Assert.IsNull(cache.GetValue(f.Skills));
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var anim=ScriptableObject.CreateInstance<SkillAnimationAsset>();
            try{
                using(var edit=new SerializedObject(root)){edit.FindProperty("animation").objectReferenceValue=anim;edit.ApplyModifiedPropertiesWithoutUndo();}
                var runner=new SkillRunner(new AbilitySystemComponent(),()=>f.Skills.Driver);
                Assert.AreNotEqual(AbilityActivationResult.Activated,runner.TryCast(root));Assert.IsNull(cache.GetValue(f.Skills));
            }finally{Object.DestroyImmediate(root);Object.DestroyImmediate(anim);}
            Assert.IsTrue(d.IsValid,"authored exit owns only its remaining visual fade");
            Step(d,.1f);Assert.That(f.Weapon.localPosition.x,Is.InRange(2.1f,7.95f));
            Step(d,.2f);Assert.IsFalse(d.IsValid);Assert.AreEqual(2,f.Weapon.localPosition.x,.03f);
            f.Skills.enabled=true;Assert.AreNotSame(d,f.Skills.Driver);
        }
        [UnityTest] public IEnumerator InspectorNotificationDoesNotInterruptCurrentPlayback(){
            using var f=new Fixture(false);yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            d.Play("skill",1,false);yield return null;yield return null;
            f.Skills.SendMessage("OnValidate");Assert.AreSame(d,f.Skills.Driver);Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            f.Backend.additive=true;f.Skills.SendMessage("OnValidate");Assert.IsTrue(d.IsValid);
            Assert.AreNotSame(d,f.Skills.Driver,"changed backend settings apply at the next request");
        }
        [UnityTest] public IEnumerator AnimationCallbackCanWarmAndPlayANewlyBoundClip(){
            using var f=new Fixture(false);var callback=f.Go.AddComponent<RootMotionCallbackProbe>();f.Backend.rootMotion=true;
            var extra=f.Bone("Extra",1);var next=f.Clip("Next","Extra",12);yield return null;
            var d=(PlayableAnimationDriver)f.Skills.Driver;new AnimationTask(new SkillAnimationDefinition("skill",1,blendOut:.05f),d);
            var evaluating=typeof(PlayableAnimationDriver).GetField("_evaluating",Private);
            bool ran=false,continued=false;float duration=0;
            callback.OnApply=()=>{
                if(ran || !(bool)evaluating.GetValue(d))return;
                ran=true;
                var enqueue=typeof(PlayableAnimationDriver).GetMethod("Enqueue",Private);
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("InvalidOperationException: queued test fault"));
                enqueue.Invoke(d,new object[]{(Action)(()=>throw new InvalidOperationException("queued test fault"))});
                enqueue.Invoke(d,new object[]{(Action)(()=>continued=true)});
                f.Skills.enabled=false;f.Skills.enabled=true;Assert.AreSame(d,f.Skills.Driver,"same-sample re-enable must cancel pending retirement");
                d.Prewarm(next);d.SetNextClip(next);duration=d.Play("skill",.7f,false);d.Seek(.25f);
                Assert.GreaterOrEqual(((ICollection)typeof(PlayableAnimationDriver).GetField("_afterSample",Private).GetValue(d)).Count,3);
                Assert.AreEqual(1,extra.localPosition.x,.03f,"none of the queued commands can sample the new pose in this callback");
            };
            for(int i=0;i<12 && !ran;i++)yield return null;Assert.IsTrue(ran,"positive control: callback inside actual manual graph evaluation");
            Assert.IsTrue(continued,"the next operation was dispatched in the same drain despite the failure");
            yield return new WaitForSeconds(.12f);yield return null;Assert.AreEqual(.7f,duration,.001f);Assert.AreEqual(12,extra.localPosition.x,.03f);
        }
        [UnityTest] public IEnumerator NativeControllerCallbackDefersGraphChangesUntilSamplingEnds(){
            using var f=new Fixture(false);var callback=f.Go.AddComponent<RootMotionCallbackProbe>();f.Backend.rootMotion=true;
            var next=f.Clip("NativeNext","Extra",12);var extra=f.Bone("Extra",1);
            yield return null;var driver=(PlayableAnimationDriver)f.Skills.Driver;driver.Play("skill",1,false);
            var sampling=typeof(SkillAnimationManager).GetProperty("NativeSampling",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            bool ran=false;
            callback.OnApply=()=>{
                if(ran || !(bool)sampling.GetValue(null))return;
                ran=true;driver.Prewarm(next);driver.SetNextClip(next);driver.Play("skill",1,false);
                Assert.Greater(((ICollection)typeof(PlayableAnimationDriver).GetField("_afterSample",Private).GetValue(driver)).Count,0);
            };
            for(int i=0;i<15 && !ran;i++)yield return null;
            Assert.IsTrue(ran,"positive control: Unity dispatched a native animation callback");yield return null;yield return null;
            Assert.AreEqual(12,extra.localPosition.x,.03f);
        }
        [UnityTest] public IEnumerator FirstWarmAndCastFromNativeCallbackCreateNoGraphUntilAfterSampling(){
            using var f=new Fixture(false);var probe=f.Go.AddComponent<RootMotionCallbackProbe>();
            var ability=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var animation=ScriptableObject.CreateInstance<SkillAnimationAsset>();
            var owner=new AbilitySystemComponent();var runner=new SkillRunner(owner,()=>f.Skills.Driver);
            try{
                using(var a=new SerializedObject(animation)){a.FindProperty("clipName").stringValue="skill";a.FindProperty("blendIn").floatValue=0;a.ApplyModifiedPropertiesWithoutUndo();}
                using(var a=new SerializedObject(ability)){a.FindProperty("animation").objectReferenceValue=animation;a.ApplyModifiedPropertiesWithoutUndo();}
                var graph=typeof(PlayableAnimationDriver).GetField("_graph",Private);PlayableAnimationDriver d=null;bool ran=false;
                probe.OnNative=()=>{
                    if(ran)return;ran=true;f.Skills.Prewarm();
                    Assert.AreEqual(AbilityActivationResult.Activated,runner.TryCast(ability));d=(PlayableAnimationDriver)f.Skills.Driver;
                    Assert.IsFalse(((UnityEngine.Playables.PlayableGraph)graph.GetValue(d)).IsValid(),"no graph in the first native callback");
                };
                for(int i=0;i<15 && !ran;i++)yield return null;
                Assert.IsTrue(ran);yield return null;yield return null;
                Assert.IsTrue(((UnityEngine.Playables.PlayableGraph)graph.GetValue(d)).IsValid());Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            }finally{owner.Clear();Object.DestroyImmediate(ability);Object.DestroyImmediate(animation);}
        }
        [UnityTest] public IEnumerator ReenableDuringAnAuthoredExitBlendsFromTheVisiblePose(){
            using var f=new Fixture(false);yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            new AnimationTask(new SkillAnimationDefinition("skill",1,blendOut:.4f),d);yield return null;yield return null;
            f.Skills.enabled=false;Step(d,.1f);float prior=f.Weapon.localPosition.x;Assert.That(prior,Is.InRange(2.5f,7.95f));
            f.Skills.enabled=true;Assert.AreSame(d,f.Skills.Driver);d.SetNextClip(f.Clip("Next","Weapon",12));d.Play("skill",1,false);
            Step(d,0);Assert.AreEqual(prior,f.Weapon.localPosition.x,.07f);Step(d,.15f);Assert.That(f.Weapon.localPosition.x,Is.InRange(prior+.1f,11.95f));
            Step(d,.23f);Assert.AreEqual(12,f.Weapon.localPosition.x,.03f);
        }
        [UnityTest] public IEnumerator ReclaimInsideSamplingStillStopsWithoutAnotherCast(){
            using var f=new Fixture(false);var relay=f.Go.AddComponent<RootMotionCallbackProbe>();f.Backend.rootMotion=true;
            yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            new AnimationTask(new SkillAnimationDefinition("skill",1,blendOut:.3f),d);
            yield return null;yield return null;Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            bool reclaimed=false;
            relay.OnApply=()=>{
                if(reclaimed || !(bool)typeof(PlayableAnimationDriver).GetField("_evaluating",Private).GetValue(d))return;
                reclaimed=true;f.Skills.enabled=false;f.Skills.enabled=true;Assert.AreSame(d,f.Skills.Driver);
            };
            for(int i=0;i<10 && !reclaimed;i++)yield return null;
            Assert.IsTrue(reclaimed);Step(d,.4f);
            Assert.AreEqual(2,f.Weapon.localPosition.x,.03f);Assert.AreEqual(0,d.CopyWeights(new ClipWeight[2]));
            d.Play("skill",1,false,0);Step(d,0);
            Assert.AreEqual(8,f.Weapon.localPosition.x,.03f,"A finished exit cannot extend a later instant entry.");
        }
        [UnityTest] public IEnumerator PrewarmAfterEquippingFindsNewNestedChannelsAndKeepsOldOnes(){
            using var f=new Fixture(false);yield return null;
            var clip=f.Clip("ChangingRig","Arm",10);
            AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve("Weapon/Grip",typeof(Transform),"m_LocalPosition.y"),AnimationCurve.Constant(0,1,6));
            var driver=(PlayableAnimationDriver)f.Skills.Driver;driver.Prewarm(clip);
            var grip=new GameObject("Grip").transform;grip.SetParent(f.Weapon,false);grip.localPosition=Vector3.up*2;
            driver.Prewarm(clip);driver.SetNextClip(clip);driver.Play("skill",1,false);Step(driver,0);
            Assert.AreEqual(10,f.Arm.localPosition.x,.04f);Assert.AreEqual(6,grip.localPosition.y,.04f);
            driver.Stop(.05f);Step(driver,.1f);Assert.AreEqual(2,grip.localPosition.y,.04f);
        }
        [UnityTest] public IEnumerator ControllerReclaimsAMovingArmDuringTheWeaponOnlySkill(){
            using var f=new Fixture(false);
            var idle=f.Controller.animationClips[0];AnimationUtility.SetEditorCurve(idle,EditorCurveBinding.FloatCurve("Arm",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Linear(0,0,1,20));
            yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            d.SetNextClip(f.Clip("ArmAttack","Arm",14));d.Play("skill",1,false);
            yield return null;yield return null;Assert.AreEqual(14,f.Arm.localPosition.x,.01f);
            d.Play("skill",1,false,new SkillBlend(.05f,mode:SkillBlendMode.Inertialize));Step(d,.06f);
            foreach(var at in new[]{.7f,.17f,.4f,.73f}){
                f.Anim.Play("Idle",0,at);f.Anim.Update(0);float native=f.Arm.localPosition.x;
                Assert.AreEqual(at*20,native,.03f);Step(d,.01f);
                Assert.AreEqual(native,f.Arm.localPosition.x,.03f);Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            }
        }
        [UnityTest] public IEnumerator FinalStopPublishesANativeValueEqualToTheLastSkillOutput(){
            using var f=new Fixture(false);
            var idle=f.Controller.animationClips[0];
            AnimationUtility.SetEditorCurve(idle,EditorCurveBinding.FloatCurve("Arm",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Linear(0,0,1,20));
            yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            d.SetNextClip(f.Clip("ArmAttack","Arm",14));d.Play("skill",1,false);
            yield return null;yield return null;Assert.AreEqual(14,f.Arm.localPosition.x,.03f);
            d.Stop(.05f);f.Anim.Play("Idle",0,.7f);f.Anim.Update(0);Assert.AreEqual(14,f.Arm.localPosition.x,.03f);
            Step(d,.06f);Assert.AreEqual(14,f.Arm.localPosition.x,.03f,"before another native tick can hide a bad final handoff");
        }
        [UnityTest] public IEnumerator UnboundWeaponReturnsTheLatestEquipmentPoseOnEveryReleasePath(){
            foreach(bool defaults in new[]{true,false}){
                using var f=new Fixture(defaults);yield return null;var driver=(PlayableAnimationDriver)f.Skills.Driver;
                driver.Prewarm(f.WeaponClip); // Rig and Animator both started with Weapon=2.
                for(int path=0;path<5;path++){
                    float equipment=3.25f+path*.1f;f.Weapon.localPosition=Vector3.right*equipment;
                    driver=(PlayableAnimationDriver)f.Skills.Driver;driver.Play("skill",1,false);yield return null;yield return null;
                    Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
                    if(path==0)f.Skills.enabled=false;
                    if(path==1)f.Skills.SetBackend(new AnimatorBackend());
                    if(path==2)f.Go.SetActive(false);
                    if(path==3){f.Anim.cullingMode=AnimatorCullingMode.CullCompletely;driver.Stop(.03f);yield return new WaitForSeconds(.12f);}
                    if(path==4){
                        f.Weapon.localPosition=Vector3.right*6.25f;yield return null;yield return null;
                        Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);equipment=6.25f;
                        driver.Stop(new SkillBlend(.06f,mode:SkillBlendMode.Inertialize));
                        yield return new WaitForSeconds(.12f);
                    }
                    Assert.AreEqual(equipment,f.Weapon.localPosition.x,.03f,"immediate handoff path="+path+" WD="+defaults);
                    f.Go.SetActive(true);f.Skills.enabled=true;f.Skills.SetBackend(f.Backend);
                    f.Anim.cullingMode=AnimatorCullingMode.AlwaysAnimate;yield return null;yield return null;
                    Assert.AreEqual(equipment,f.Weapon.localPosition.x,.03f,"after re-enable path="+path);
                }
            }
        }
        internal static Avatar Human(GameObject root,Action<Transform> attachRightHand=null){
            var skeleton=new List<SkeletonBone>();var mapping=new List<HumanBone>();
            Transform Add(HumanBodyBones id,Transform parent,float x,float y,float z=0){
                var t=new GameObject(id.ToString()).transform;t.SetParent(parent,false);t.localPosition=new Vector3(x,y,z);
                mapping.Add(new HumanBone{boneName=t.name,humanName=HumanTrait.BoneName[(int)id],limit=new HumanLimit{useDefaultValues=true}});return t;
            }
            var hip=Add(HumanBodyBones.Hips,root.transform,0,1);var spine=Add(HumanBodyBones.Spine,hip,0,.24f);
            var chest=Add(HumanBodyBones.Chest,spine,0,.23f);var neck=Add(HumanBodyBones.Neck,chest,0,.17f);Add(HumanBodyBones.Head,neck,0,.13f);
            for(int side=0;side<2;side++){
                bool l=side==0;float dir=l?-1:1;
                var leg=Add(l?HumanBodyBones.LeftUpperLeg:HumanBodyBones.RightUpperLeg,hip,dir*.12f,-.05f);
                var shin=Add(l?HumanBodyBones.LeftLowerLeg:HumanBodyBones.RightLowerLeg,leg,0,-.42f);
                Add(l?HumanBodyBones.LeftFoot:HumanBodyBones.RightFoot,shin,0,-.4f,.06f);
                var arm=Add(l?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm,chest,dir*.16f,.11f);
                var elbow=Add(l?HumanBodyBones.LeftLowerArm:HumanBodyBones.RightLowerArm,arm,dir*.29f,0);
                var hand=Add(l?HumanBodyBones.LeftHand:HumanBodyBones.RightHand,elbow,dir*.24f,0);if(!l)attachRightHand?.Invoke(hand);
            }
            foreach(var t in root.GetComponentsInChildren<Transform>())skeleton.Add(new SkeletonBone{
                name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale});
            var avatar=AvatarBuilder.BuildHumanAvatar(root,new HumanDescription{human=mapping.ToArray(),skeleton=skeleton.ToArray(),
                upperArmTwist=.5f,lowerArmTwist=.5f,upperLegTwist=.5f,lowerLegTwist=.5f,armStretch=.05f,legStretch=.05f});
            Assert.IsTrue(avatar.isValid && avatar.isHuman);return avatar;
        }
        [UnityTest] public IEnumerator ExposedWeaponReturnsOnHumanoidAndOptimizedAvatars(){
            foreach(bool optimize in new[]{false,true}){
                using var f=new Fixture(false);
                for(int j=0;j<180;j++)f.Bone("Accessory"+j,0);
                var avatar=Human(f.Go);
                try{
                    f.Anim.avatar=avatar;
                    if(optimize)AnimatorUtility.OptimizeTransformHierarchy(f.Go,new[]{"Arm","Weapon"});
                    f.Anim.Rebind();yield return null;
                    Assert.IsTrue(f.Anim.isHuman);Assert.AreEqual(!optimize,f.Anim.hasTransformHierarchy);
                    var weapon=f.Go.transform.Find("Weapon");Assert.IsNotNull(weapon);
                    for(int i=0;i<2;i++){
                        float rest=3.5f+i;
                        // Optimized avatars may write their exposed bind pose even with a controller
                        // containing no Weapon curve. Measure that native-only pose independently.
                        weapon.localPosition=Vector3.right*rest;yield return null;yield return null;
                        float native=weapon.localPosition.x;Assert.AreNotEqual(8,native);
                        if(optimize)Assert.AreNotEqual(rest,native,"the next native frame must not be mistaken for a successful handoff");
                        weapon.localPosition=Vector3.right*rest;
                        var driver=(PlayableAnimationDriver)f.Skills.Driver;driver.Play("skill",1,false);
                        var extras=typeof(PlayableAnimationDriver).GetField("_extras",Private).GetValue(driver);
                        var watched=(ICollection)extras.GetType().GetField("_entries",Private).GetValue(extras);
                        Assert.AreEqual(1,watched.Count,"the skill only writes Weapon; decorative transforms should not cost two reads per sample");
                        yield return null;yield return null;Assert.AreEqual(8,weapon.localPosition.x,.04f,"positive skill sample");
                        if(i==0){driver.Stop(.04f);Step(driver,.05f);
                            Assert.AreEqual(rest,weapon.localPosition.x,.04f,"handoff in the final sample, before the next native Animator tick");
                            yield return new WaitForSeconds(.1f);}else f.Skills.enabled=false;
                        Assert.AreEqual(i==1?rest:native,weapon.localPosition.x,.04f,"handoff, optimized="+optimize+" disable="+(i==1));
                        yield return null;yield return null;Assert.AreEqual(native,weapon.localPosition.x,.04f,"native-only pose after handoff");
                    }
                }finally{f.Skills.enabled=false;if(optimize)AnimatorUtility.DeoptimizeTransformHierarchy(f.Go);f.Anim.avatar=null;Object.DestroyImmediate(avatar);}
            }
        }
        [UnityTest] public IEnumerator DefaultsOffDoesNotMistakeSkillOutputForANativeWeaponPose(){
            using var f=new Fixture(false,true);yield return null;
            f.Anim.Play("Draw");yield return null;yield return null;Assert.AreEqual(5,f.Weapon.localPosition.x,.03f);
            f.Anim.Play("Idle");yield return null;var driver=(PlayableAnimationDriver)f.Skills.Driver;
            driver.Play("skill",1,false);yield return null;yield return null;Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            driver.Stop(.04f);yield return new WaitForSeconds(.13f);Assert.AreEqual(5,f.Weapon.localPosition.x,.03f);
        }
        [UnityTest] public IEnumerator EquipmentWrittenOnASkippedFrameSurvivesTheFourFrameHold(){
            using var f=new Fixture(false);yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            d.UpdateRate=new SkillAnimationRate{mode=SkillAnimationFrequency.EveryNFrames,frames=4};
            d.Play("skill",1,true);Step(d,.01f);Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            var step=typeof(PlayableAnimationDriver).GetMethod("ScheduledUpdate",Private);
            var observe=typeof(PlayableAnimationDriver).GetMethod("ObserveBeforeAnimator",Private);
            int phase=(int)typeof(PlayableAnimationDriver).GetField("_phase",Private).GetValue(d);
            long skipped=(1L-phase)%4;long count=SkillAnimationManager.EvaluationCount;
            f.Weapon.localPosition=Vector3.right*5.25f;
            observe.Invoke(d,new object[]{false});f.Anim.Update(.016f);step.Invoke(d,new object[]{skipped,.016f,null});
            Assert.AreEqual(count,SkillAnimationManager.EvaluationCount,"this must be a real skipped sample");
            Assert.AreEqual(8,f.Weapon.localPosition.x,.03f,"the held pose covers the equipment edit");
            d.Stop(0);Step(d,.016f);Assert.AreEqual(5.25f,f.Weapon.localPosition.x,.03f);
        }
        [UnityTest] public IEnumerator LosingControllerCoverageDuringPlaybackStillReturnsTheOwnedChannel(){
            using var f=new Fixture(false);
            foreach(string axis in new[]{"y","z"})AnimationUtility.SetEditorCurve(f.Controller.animationClips[0],
                EditorCurveBinding.FloatCurve("Arm",typeof(Transform),"m_LocalPosition."+axis),AnimationCurve.Constant(0,1,0));
            var attack=f.Clip("ArmAttack","Arm",14);
            var other=AnimatorController.CreateAnimatorControllerAtPath(f.Folder+"/Other.controller");
            other.layers[0].stateMachine.AddState("Other").motion=f.WeaponClip;
            yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            float native=f.Arm.localPosition.x;Assert.AreEqual(4,native,.03f);
            d.SetNextClip(attack);d.Play("skill",1,true);Step(d,.01f);
            Assert.AreEqual(14,f.Arm.localPosition.x,.03f);
            var watched=typeof(PlayableAnimationDriver).GetProperty("WatchedGenericBones",Private);
            Assert.AreEqual(0,watched.GetValue(d));
            f.Anim.runtimeAnimatorController=other;Step(d,.01f);
            Assert.AreEqual(1,watched.GetValue(d),"controller swap must promote the live arm to tracked");
            d.Stop(0);Step(d,.016f);Assert.AreEqual(native,f.Arm.localPosition.x,.03f);
        }
        [UnityTest] public IEnumerator PartialNativeTranslationCannotHideAnUnboundSkillAxis(){
            using var f=new Fixture(false);var attack=f.Clip("ArmAttack","Arm",14);
            AnimationUtility.SetEditorCurve(attack,EditorCurveBinding.FloatCurve("Arm",typeof(Transform),"m_LocalPosition.y"),AnimationCurve.Constant(0,1,7));
            yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            f.Arm.localPosition+=Vector3.up*3;d.SetNextClip(attack);d.Play("skill",1,false);Step(d,.01f);
            Assert.AreEqual(7,f.Arm.localPosition.y,.03f);
            Assert.AreEqual(1,typeof(PlayableAnimationDriver).GetProperty("WatchedGenericBones",Private).GetValue(d));
            d.Stop(0);Step(d,.016f);Assert.AreEqual(3,f.Arm.localPosition.y,.03f);
        }
        [UnityTest] public IEnumerator EquipmentCanEditAnUnanimatedAxisDuringASkill(){
            using var f=new Fixture(false);yield return null;var driver=(PlayableAnimationDriver)f.Skills.Driver;
            f.Weapon.localPosition=Vector3.right*3.5f;driver.Play("skill",1,false);yield return null;yield return null;
            var p=f.Weapon.localPosition;Assert.AreEqual(8,p.x,.03f);p.y=4.25f;f.Weapon.localPosition=p;
            var rotation=Quaternion.Euler(15,35,50);f.Weapon.localRotation=rotation;f.Weapon.localScale=new Vector3(1,1,1.5f);
            yield return null;yield return null;driver.Stop(.03f);yield return new WaitForSeconds(.12f);
            Assert.AreEqual(3.5f,f.Weapon.localPosition.x,.03f);Assert.AreEqual(4.25f,f.Weapon.localPosition.y,.03f);
            Assert.Less(Quaternion.Angle(rotation,f.Weapon.localRotation),.1f);Assert.AreEqual(1.5f,f.Weapon.localScale.z,.01f);
        }
        [UnityTest] public IEnumerator EquipmentOwnsTheReleasedWeaponWhileTheNextSkillKeepsPlaying(){
            using var f=new Fixture(false);yield return null;var driver=(PlayableAnimationDriver)f.Skills.Driver;
            f.Weapon.localPosition=Vector3.right*3.5f;driver.Play("skill",1,false);yield return null;yield return null;
            Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            driver.SetNextClip(f.Clip("Follow","Arm",7));driver.Play("skill",1,false,new SkillBlend(.04f,mode:SkillBlendMode.Inertialize));
            yield return new WaitForSeconds(.1f);
            Assert.AreEqual(7,f.Arm.localPosition.x,.03f);Assert.AreEqual(3.5f,f.Weapon.localPosition.x,.03f);
            f.Weapon.localPosition=new Vector3(6.25f,4.25f,0);
            for(int frame=0;frame<4;frame++){
                yield return null;Assert.AreEqual(7,f.Arm.localPosition.x,.03f);
                Assert.AreEqual(6.25f,f.Weapon.localPosition.x,.03f);Assert.AreEqual(4.25f,f.Weapon.localPosition.y,.03f);
            }
            driver.Stop(0);Step(driver,.01f);driver.Dispose();
            Assert.AreEqual(6.25f,f.Weapon.localPosition.x,.03f,"detaching the graph cannot take the weapon back");
        }
        [UnityTest] public IEnumerator ActiveAimFollowsNewTargetAfterTheWeaponClipStops(){
            using var f=new Fixture(false);yield return null;var d=(PlayableAnimationDriver)f.Skills.Driver;
            var spec=new AbilitySystemComponent().GrantAbility(new GameplayAbilityDefinition());
            var c=new SkillExecutionContext(null,spec,null,new EffectEvaluationContext(1),new TimelineTaskContext{TargetPosition=new Vector3(10,0,0)});
            d.Play("skill",1,false);yield return null;yield return null;
            using var aim=d.Aim(c,"Weapon");Assert.NotNull(aim);aim.Sample(1);Step(d,.01f);
            Assert.Greater(f.Weapon.forward.x,.9f);
            d.Stop(.04f);Step(d,.06f);Assert.AreEqual(2,f.Weapon.localPosition.x,.03f);
            f.Weapon.localPosition=Vector3.right*5.5f;
            c.Services.TargetPosition=new Vector3(5.5f,10,0);aim.Sample(1);
            for(int i=0;i<3;i++){
                yield return null;Assert.AreEqual(5.5f,f.Weapon.localPosition.x,.03f);
                Assert.Greater(f.Weapon.forward.y,.9f,"Aim must still turn after the old clip released its channels");
            }
        }
        [UnityTest] public IEnumerator OrdinaryReleaseKeepsItsBonesWhenAnotherClipGrowsTheRig(){
            using var f=new Fixture(false);yield return null;var driver=(PlayableAnimationDriver)f.Skills.Driver;
            f.Weapon.localPosition=Vector3.right*3.5f;driver.Play("skill",1,false);
            yield return null;yield return null;Assert.AreEqual(8,f.Weapon.localPosition.x,.03f);
            driver.Stop(.25f);Step(driver,.1f);Assert.That(f.Weapon.localPosition.x,Is.InRange(3.6f,7.95f),"growing an actually fading pose");
            f.Bone("Extra",1);driver.Prewarm(f.Clip("More","Extra",12));Step(driver,.16f);yield return null;yield return null;
            Assert.AreEqual(3.5f,f.Weapon.localPosition.x,.03f);
        }
#endif
    }
}
