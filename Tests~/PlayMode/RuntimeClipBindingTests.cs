using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

namespace ZFramework.GAS.Tests
{
    public sealed class RuntimeClipBindingTests : IPrebuildSetup,IPostBuildCleanup
    {
        private const string Prefix="GASDynamicClipFixture",SessionKey="GAS.Tests.DynamicClipFixture";
        private const string WeaponPath="Hips/Spine/Chest/RightUpperArm/RightLowerArm/RightHand/Weapon";
        public void Setup()
        {
#if UNITY_EDITOR
            var folder=AssetDatabase.GenerateUniqueAssetPath("Assets/"+Prefix);
            var guid=AssetDatabase.CreateFolder("Assets",System.IO.Path.GetFileName(folder));
            SessionState.SetString(SessionKey,guid);AssetDatabase.CreateFolder(folder,"Resources");
            var dir=folder+"/Resources/";
            AnimationClip Clip(string name,string path,float value)
            {
                var clip=new AnimationClip();AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(path,typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Constant(0,1,value));
                AssetDatabase.CreateAsset(clip,dir+Prefix+name+".anim");return clip;
            }
            var a=Clip("Original","A",7);Clip("Replacement","B",9);
            var go=new GameObject("rig");
            try
            {
                foreach(string name in new[]{"A","B"}){var t=new GameObject(name).transform;t.SetParent(go.transform);t.localPosition=Vector3.right*2;}
                go.AddComponent<Animator>();go.AddComponent<SkillAnimator>().SetBackend(new PlayableAnimationBackend{
                    clips=new[]{new PlayableClipBinding{name="skill",clip=a,posePaths=new[]{"obsolete"}}}});
                PrefabUtility.SaveAsPrefabAsset(go,dir+Prefix+"Rig.prefab");
                var cfg=(PlayableAnimationBackend)go.GetComponent<SkillAnimator>().Backend;
                var baseClip=Clip("Idle","B",4);var overlay=Clip("Overlay","A",22);
                // Unity samples a bound translation as a vector even if only X was authored.
                // Author all three base channels to guarantee native ownership of this path.
                foreach(string axis in new[]{"y","z"})AnimationUtility.SetEditorCurve(baseClip,
                    EditorCurveBinding.FloatCurve("B",typeof(Transform),"m_LocalPosition."+axis),AnimationCurve.Constant(0,1,0));
                AnimationUtility.SetEditorCurve(overlay,EditorCurveBinding.FloatCurve("B",typeof(Transform),"m_LocalPosition.x"),AnimationCurve.Linear(0,4,1,14));
                var swapped=AnimatorController.CreateAnimatorControllerAtPath(dir+Prefix+"Masked.controller");
                swapped.layers[0].stateMachine.AddState("Idle").motion=baseClip;swapped.AddLayer("Overlay");
                var layerMask=new AvatarMask{transformCount=2};layerMask.SetTransformPath(0,"A");layerMask.SetTransformActive(0,false);
                layerMask.SetTransformPath(1,"B");layerMask.SetTransformActive(1,true);AssetDatabase.CreateAsset(layerMask,folder+"/Overlay.mask");
                var layers=swapped.layers;layers[1].defaultWeight=1;layers[1].avatarMask=layerMask;
                var state=layers[1].stateMachine.AddState("Overlay");state.motion=overlay;state.writeDefaultValues=false;swapped.layers=layers;
                cfg.alternativeControllers=new[]{new SkillControllerLayers{controller=swapped}};
                PrefabUtility.SaveAsPrefabAsset(go,dir+Prefix+"SwapRig.prefab");
                cfg.alternativeControllers=System.Array.Empty<SkillControllerLayers>();
                var shield=new GameObject("Shield").transform;shield.SetParent(go.transform,false);shield.localPosition=Vector3.right*2;
                for(int i=0;i<120;i++)new GameObject("Decoration"+i).transform.SetParent(go.transform,false);
                var human=SkillPoseHandoffTests.Human(go,hand=>{
                    var weapon=new GameObject("Weapon").transform;weapon.SetParent(hand,false);weapon.localPosition=Vector3.right*3;
                });AssetDatabase.CreateAsset(human,folder+"/Human.asset");go.GetComponent<Animator>().avatar=human;
                var humanClip=Clip("HumanSwap","Shield",11);
                AnimationUtility.SetEditorCurve(humanClip,EditorCurveBinding.FloatCurve("",typeof(Animator),HumanTrait.MuscleName[0]),AnimationCurve.Constant(0,1,.1f));
                Assert.IsTrue(humanClip.humanMotion,"positive control: this is a humanoid clip, not a generic transform clip");
                var nested=Clip("Nested",WeaponPath,13);
                AnimationUtility.SetEditorCurve(nested,EditorCurveBinding.FloatCurve("",typeof(Animator),HumanTrait.MuscleName[0]),AnimationCurve.Constant(0,1,.1f));
                var equipment=ScriptableObject.CreateInstance<SkillClipOverrideSet>();
                equipment.clips=new[]{new SkillClipOverrideSet.Entry{original="skill",clip=humanClip}};
                AssetDatabase.CreateAsset(equipment,dir+Prefix+"Equipment.asset");
                EditorUtility.SetDirty(humanClip);AssetDatabase.SaveAssets();
                PrefabUtility.SaveAsPrefabAsset(go,dir+Prefix+"Human.prefab");
            }
            finally{Object.DestroyImmediate(go);}
#endif
        }
        public void Cleanup()
        {
#if UNITY_EDITOR
            var guid=SessionState.GetString(SessionKey,"");var path=AssetDatabase.GUIDToAssetPath(guid);
            if(!string.IsNullOrEmpty(path) && path.StartsWith("Assets/"+Prefix,System.StringComparison.Ordinal))AssetDatabase.DeleteAsset(path);
            SessionState.EraseString(SessionKey);
#endif
        }
#if GAS_TEST_ASSET_BUNDLE
        private const string BundleName="gas-runtime-human";
#if UNITY_EDITOR
        public static void BuildWindowsCharacterBundle(){
            string dir=System.Environment.GetEnvironmentVariable("GAS_ANIMATION_BUNDLE_TEST");
            if(string.IsNullOrEmpty(dir))throw new System.InvalidOperationException("Set GAS_ANIMATION_BUNDLE_TEST to an output directory.");
            var fixture=new RuntimeClipBindingTests();
            try{
                fixture.Setup();System.IO.Directory.CreateDirectory(dir);
                string folder=AssetDatabase.GUIDToAssetPath(SessionState.GetString(SessionKey,""))+"/Resources/"+Prefix;
                var build=new AssetBundleBuild{assetBundleName=BundleName,assetNames=new[]{folder+"Human.prefab",folder+"Equipment.asset"},addressableNames=new[]{"actor","equipment"}};
                if(BuildPipeline.BuildAssetBundles(dir,new[]{build},BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneWindows64)==null)
                    throw new System.InvalidOperationException("Building animation bundle failed.");
            }finally{fixture.Cleanup();}
        }
#endif
        [UnityTest,Explicit("Build the content bundle and run in the Windows standalone player.")]
        public IEnumerator EquipmentAndCharacterFromAContentBundleTrackOnlyTheAnimatedAttachment(){
            Assert.AreEqual(RuntimePlatform.WindowsPlayer,Application.platform);
            var bundle=AssetBundle.LoadFromFile(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("GAS_ANIMATION_BUNDLE_TEST"),BundleName));Assert.IsNotNull(bundle);
            var go=Object.Instantiate(bundle.LoadAsset<GameObject>("actor"));
            try{
                AnimatorUtility.OptimizeTransformHierarchy(go,new[]{"A","Shield","Decoration0","Decoration1"});
                var skills=go.GetComponent<SkillAnimator>();var gear=bundle.LoadAsset<SkillClipOverrideSet>("equipment");
                Assert.IsTrue(gear.clips[0].clip.humanMotion);skills.PushOverride(gear);
                var d=(PlayableAnimationDriver)skills.Driver;new AnimationTask(new SkillAnimationDefinition("skill",1),d);
                Assert.AreEqual(1,d.TrackedAttachmentCount);yield return null;yield return null;
                var shield=go.transform.Find("Shield");Assert.AreEqual(11,shield.localPosition.x,.05f);
                d.Stop(0);yield return null;yield return null;Assert.AreEqual(2,shield.localPosition.x,.05f);
            }finally{Object.DestroyImmediate(go);bundle.Unload(true);}
        }
#endif
        [UnityTest] public IEnumerator RuntimeHumanPrefabTracksOnlyClipTargetsAndExplicitExtras(){
            var prefab=Resources.Load<GameObject>(Prefix+"Human");Assert.IsNotNull(prefab);
            var clip=Resources.Load<AnimationClip>(Prefix+"HumanSwap");Assert.IsTrue(clip!=null && clip.humanMotion);
            foreach(bool optimize in new[]{false,true}){
                var go=Object.Instantiate(prefab);
                try{
                    if(optimize)AnimatorUtility.OptimizeTransformHierarchy(go,new[]{"A","Shield","Decoration0","Decoration1","Decoration2"});
                    var anim=go.GetComponent<Animator>();Assert.IsTrue(anim.isHuman);Assert.AreEqual(!optimize,anim.hasTransformHierarchy);
                    var skills=go.GetComponent<SkillAnimator>();var config=(PlayableAnimationBackend)skills.Backend;
                    config.exposedAttachments=new[]{go.transform.Find("A")};var shield=go.transform.Find("Shield");Assert.IsNotNull(shield);
                    var d=(PlayableAnimationDriver)skills.Driver;d.SetNextClip(clip);d.Play("skill",1,false);
                    Assert.AreEqual(2,d.TrackedAttachmentCount,"only the replacement Shield and the explicit A");
                    yield return null;yield return null;Assert.AreEqual(11,shield.localPosition.x,.05f);
                    d.Stop(.05f);yield return new WaitForSeconds(.12f);Assert.AreEqual(2,shield.localPosition.x,.05f);
                    Assert.AreEqual(0,d.TrackedAttachmentCount);
                    config.exposedAttachments=System.Array.Empty<Transform>();skills.SetBackend(config);
                    d=(PlayableAnimationDriver)skills.Driver;d.SetNextClip(clip);d.Play("skill",1,false);Assert.AreEqual(1,d.TrackedAttachmentCount);
                }finally{Object.Destroy(go);}
            }
        }
        [UnityTest] public IEnumerator UnknownOptimizedPathsStillReturnAnUnlistedNestedWeapon(){
            var go=Object.Instantiate(Resources.Load<GameObject>(Prefix+"Human"));
            try{
                Assert.NotNull(go.transform.Find(WeaponPath),"positive control: nested under the authored right hand");
                AnimatorUtility.OptimizeTransformHierarchy(go,new[]{"Shield","Weapon","Decoration0"});
                Assert.IsFalse(go.GetComponent<Animator>().hasTransformHierarchy);
                var weapon=go.transform.Find("Weapon");Assert.NotNull(weapon,"optimized exposed attachment");
                var clip=Resources.Load<AnimationClip>(Prefix+"Nested");var host=go.GetComponent<SkillAnimator>();
                host.SetBackend(new PlayableAnimationBackend{clips=new[]{new PlayableClipBinding{name="skill",clip=clip}},
                    exposedAttachments=new[]{go.transform.Find("Shield")}});
                var d=(PlayableAnimationDriver)host.Driver;
                yield return null;yield return null;var rest=weapon.localPosition;d.Play("skill",1,false);
                Assert.GreaterOrEqual(d.TrackedAttachmentCount,2,"the declared Shield must not exclude the animated Weapon");
#if !UNITY_EDITOR
                Assert.Greater(d.TrackedAttachmentCount,2,"no prefab path metadata in this dynamic backend: unknown exposures stay protected");
#endif
                yield return null;yield return null;Assert.Greater(Vector3.Distance(rest,weapon.localPosition),3,
                    "after optimization the exposed bone is in animator space, not right-hand local space");
                d.Stop(0);yield return null;yield return null;Assert.Less(Vector3.Distance(rest,weapon.localPosition),.06f);
            }finally{Object.Destroy(go);}
        }
        [UnityTest] public IEnumerator SameBackendObjectEditedDuringExitCreatesAFreshGraph(){
            var go=Object.Instantiate(Resources.Load<GameObject>(Prefix+"Rig"));
            try{
                var host=go.GetComponent<SkillAnimator>();var cfg=(PlayableAnimationBackend)host.Backend;
                var old=(PlayableAnimationDriver)host.Driver;new AnimationTask(new SkillAnimationDefinition("skill",1,blendOut:2),old);
                yield return null;yield return null;Assert.AreEqual(7,go.transform.Find("A").localPosition.x,.04f);
                host.enabled=false;host.enabled=true;Assert.IsTrue(old.IsValid,"test must run during a live fade");
                cfg.clips[0]=new PlayableClipBinding{name="changed",clip=Resources.Load<AnimationClip>(Prefix+"Replacement")};
                cfg.exposedAttachments=new[]{go.transform.Find("B")};host.SetBackend(cfg);
                var next=(PlayableAnimationDriver)host.Driver;Assert.AreNotSame(old,next);Assert.IsFalse(old.IsValid);
                Assert.Greater(next.Play("changed",1,false),0);next.Seek(.5f);Assert.AreEqual(9,go.transform.Find("B").localPosition.x,.03f);
            }finally{Object.Destroy(go);}
        }
        [UnityTest] public IEnumerator ImportedControllerCoverageSkipsOnlyTheGuaranteedChannels(){
            var go=Object.Instantiate(Resources.Load<GameObject>(Prefix+"SwapRig"));
            try{
                var anim=go.GetComponent<Animator>();anim.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                anim.runtimeAnimatorController=Resources.Load<RuntimeAnimatorController>(Prefix+"Masked");
                yield return null;yield return null;
                var d=(PlayableAnimationDriver)go.GetComponent<SkillAnimator>().Driver;
                const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                int Watched()=>(int)typeof(PlayableAnimationDriver).GetProperty("WatchedGenericBones",flags).GetValue(d);
                bool Job()=>(bool)typeof(PlayableAnimationDriver).GetProperty("HandoffJobConnected",flags).GetValue(d);
                var b=go.transform.Find("B");
                d.SetNextClip(Resources.Load<AnimationClip>(Prefix+"Replacement"));d.Play("skill",1,true);
                yield return null;yield return null;Assert.AreEqual(9,b.localPosition.x,.03f);
                Assert.Zero(Watched(),"B.x is written by every possible base state; this needs the baked coverage in a player");Assert.IsFalse(Job());
                d.Play("skill",1,false,new SkillBlend(.25f,mode:SkillBlendMode.Inertialize));
                Assert.IsTrue(Job(),"an actual inertial transition still samples its residual");
                yield return new WaitForSeconds(.32f);Assert.AreEqual(7,go.transform.Find("A").localPosition.x,.04f);
                Assert.AreEqual(1,Watched(),"A is excluded from the controller overlay");
                Assert.IsFalse(Job(),"steady skill-only attachment tracking requires no stream job");
                d.Stop(0);yield return null;yield return null;Assert.AreEqual(2,go.transform.Find("A").localPosition.x,.03f);
            }finally{Object.Destroy(go);}
        }
        [UnityTest] public IEnumerator RuntimeControllerSwapHonorsImportedLayerMasks(){
            var go=Object.Instantiate(Resources.Load<GameObject>(Prefix+"SwapRig"));
            try{
                var anim=go.GetComponent<Animator>();var host=go.GetComponent<SkillAnimator>();var d=(PlayableAnimationDriver)host.Driver;
                var a=go.transform.Find("A");var b=go.transform.Find("B");
                anim.runtimeAnimatorController=Resources.Load<RuntimeAnimatorController>(Prefix+"Masked");
#if !UNITY_EDITOR
                var baked=((PlayableAnimationBackend)host.Backend).alternativeControllers[0];
                Assert.AreSame(anim.runtimeAnimatorController,baked.controller);
                Assert.IsTrue(baked.masks?.Length==2 && baked.masks[1]!=null,"the player must get the overlay mask from the imported prefab");
#endif
                Assert.IsNotNull(anim.runtimeAnimatorController);yield return null;yield return null;
                Assert.AreEqual(2,a.localPosition.x,.05f,"native overlay excludes A although its clip has a curve for it");
                a.localPosition=Vector3.right*3.25f;d.Play("skill",1,false);yield return null;yield return null;Assert.AreEqual(7,a.localPosition.x,.05f);
                d.Stop(new SkillBlend(.08f,mode:SkillBlendMode.Inertialize));
                yield return new WaitForSeconds(.16f);yield return null;
                Assert.AreEqual(3.25f,a.localPosition.x,.05f);
                anim.Play("Overlay",1,.6f);anim.Update(0);Assert.AreEqual(10,b.localPosition.x,.08f,"the allowed layer still drives B");
            }finally{Object.Destroy(go);}
        }
        [UnityTest] public IEnumerator ResourcesPrefabResolvesLateLoadedReplacementInPlayer()
        {
            var prefab=Resources.Load<GameObject>(Prefix+"Rig");Assert.IsNotNull(prefab);
            var go=Object.Instantiate(prefab);
            try
            {
                var driver=(PlayableAnimationDriver)go.GetComponent<SkillAnimator>().Driver;
                driver.Play("skill",1,false);driver.Seek(.5f);Assert.AreEqual(7,go.transform.Find("A").localPosition.x,.02f);
                var b=go.transform.Find("B");var clip=Resources.Load<AnimationClip>(Prefix+"Replacement");Assert.IsNotNull(clip);
                driver.SetNextClip(clip);driver.Play("skill",1,false,new SkillBlend(.2f,mode:SkillBlendMode.Inertialize));
                Assert.AreEqual(7,go.transform.Find("A").localPosition.x,.05f,"Outgoing-only bone must start from its visible pose.");
                Assert.AreEqual(1,driver.CopyWeights(new ClipWeight[4]),"The previously unknown clip must start inertially.");
                var lastTime=Time.time;int startFrame=Time.frameCount;
                yield return new WaitForSeconds(.35f);
                // Test coroutines resume before the scheduler's LateUpdate. In a slow first
                // player frame the elapsed interval may not have been sampled yet.
                yield return null;
                Assert.AreEqual(9,b.localPosition.x,.05f,$"sampled after {Time.frameCount-startFrame} frames, {Time.time-lastTime:F3}s; frame dt {Time.deltaTime:F3}");Assert.AreEqual(2,go.transform.Find("A").localPosition.x,.05f);
                driver.Stop(new SkillBlend(.2f,mode:SkillBlendMode.Inertialize));yield return new WaitForSeconds(.35f);yield return null;
                Assert.AreEqual(2,b.localPosition.x,.05f,"The replacement bone must return to its pre-cast rest pose.");
#if ENABLE_IL2CPP
                Debug.Log("GAS_POSE_IL2CPP_PASSED "+Application.platform);
#endif
            }
            finally{Object.Destroy(go);}
        }
    }
}
