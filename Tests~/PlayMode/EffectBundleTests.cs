#if GAS_TEST_ASSET_BUNDLE
using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ZFramework.GAS.Unity;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ZFramework.GAS.Tests
{
    public sealed class EffectBundleTests
    {
        const string Variable="GAS_EFFECT_BUNDLE_TEST", Bundle="effect-no-typetree";
#if UNITY_EDITOR
        // Separate build invocation: Unity cannot build an AssetBundle while building a test player.
        public static void BuildWindowsBundle()
        {
            string dir=Environment.GetEnvironmentVariable(Variable);
            if(string.IsNullOrEmpty(dir))throw new InvalidOperationException(Variable+" must name the verification output directory.");
            Directory.CreateDirectory(dir);
            string path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectBundleTest.asset");
            var effect=ScriptableObject.CreateInstance<GameplayEffectAsset>();AssetDatabase.CreateAsset(effect,path);
            try{
                using var so=new SerializedObject(effect);
                so.FindProperty("editorCopyOrigin").stringValue="fixture:101";
                so.FindProperty("editorCopyHash").stringValue="source hash";
                so.FindProperty("editorCopyLocalHash").stringValue="destination hash";
                so.FindProperty("editorCopyAcceptedHash").stringValue="accepted source revision";
                so.FindProperty("editorCopyVersion").intValue=3;
                so.FindProperty("durationPolicy").enumValueIndex=(int)EffectDurationPolicy.Duration;
                so.FindProperty("duration").floatValue=7.25f;so.FindProperty("period").floatValue=1.5f;
                so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssetIfDirty(effect);
                var content=new AssetBundleBuild{assetBundleName=Bundle,assetNames=new[]{path},addressableNames=new[]{"effect"}};
                var manifest=BuildPipeline.BuildAssetBundles(dir,new[]{content},
                    BuildAssetBundleOptions.DisableWriteTypeTree|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneWindows64);
                if(manifest==null)throw new InvalidOperationException("Failed to build the test effect bundle.");
            }finally{AssetDatabase.DeleteAsset(path);}
        }
#endif
        [Test,Explicit("Build the fixture and run in the Windows standalone player.")]
        public void StandaloneLoadsEffectWithoutTypeTree(){
            Assert.AreEqual(RuntimePlatform.WindowsPlayer,Application.platform);
            string dir=Environment.GetEnvironmentVariable(Variable);
            Assert.IsNotEmpty(dir);
            var bundle=AssetBundle.LoadFromFile(Path.Combine(dir,Bundle));Assert.IsNotNull(bundle);
            try{var effect=bundle.LoadAsset<GameplayEffectAsset>("effect");Assert.IsNotNull(effect);
                StringAssert.Contains("accepted source revision",JsonUtility.ToJson(effect));
                var def=effect.Definition;Assert.AreEqual(EffectDurationPolicy.Duration,def.DurationPolicy);
                Assert.AreEqual(7.25f,def.Duration);Assert.AreEqual(1.5f,def.Period);
            }finally{bundle.Unload(true);}
        }
    }
}
#endif
