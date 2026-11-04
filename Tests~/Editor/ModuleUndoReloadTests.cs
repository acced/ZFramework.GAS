using System.Collections;
using System.Linq;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class ModuleUndoReloadTests
    {
        [SerializeField] private string rootPath,modulePath,innerPath,guid,innerGuid,originalKey,outerKey;
        private static string EffectKey(GameplayEffectAsset effect){
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(effect,out string guid,out long id));
            Assert.AreNotEqual(0,id);Assert.AreEqual(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect)),guid);
            return AssetReferences.Key(effect);
        }
        [UnitySetUp] public IEnumerator ExtractBeforeReload()
        {
            rootPath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASReloadRoot.asset");
            modulePath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASReloadModule.asset");
            innerPath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASReloadInner.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,rootPath);
            var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"reload effect");
            originalKey=EffectKey(effect);root.activationActions=new SkillAction[]{new ApplyEffectAction{effect=effect}};
            using(var doc=new SkillDocument()){
                doc.Open(root);SkillFlowCommands.Extract(doc,root,"activationActions",0,1,modulePath,_=>true);
                var outer=((CallModuleAction)root.activationActions[0]).module;
                outerKey=EffectKey(((ApplyEffectAction)outer.actions[0]).effect);
                SkillFlowCommands.Extract(doc,outer,"actions",0,1,innerPath,_=>true);
                AssetDatabase.SaveAssetIfDirty(outer);
            }
            AssetDatabase.SaveAssetIfDirty(root);guid=AssetDatabase.AssetPathToGUID(modulePath);innerGuid=AssetDatabase.AssetPathToGUID(innerPath);
            EditorUtility.RequestScriptReload();yield return new WaitForDomainReload();
        }
        [Test] public void UndoAndRedoRestoreModuleAfterManagedDomainReload()
        {
            var root=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(rootPath);
            SkillActionModuleAsset Outer()=>((CallModuleAction)root.activationActions[0]).module;
            void Check(SkillAction action,string path){
                var effect=((ApplyEffectAction)action).effect;Assert.IsTrue(effect!=null,"Lost effect in "+path);
                Assert.AreEqual(path,AssetDatabase.GetAssetPath(effect));Assert.AreEqual("reload effect",effect.name);}
            ModuleFileUndo.Prune();
            for(int i=0;i<2;i++){
                Undo.PerformUndo();Check(Outer().actions[0],modulePath);Assert.AreEqual(outerKey,EffectKey(((ApplyEffectAction)Outer().actions[0]).effect));Assert.IsFalse(System.IO.File.Exists(innerPath));
                Undo.PerformUndo();Check(root.activationActions[0],rootPath);Assert.AreEqual(originalKey,EffectKey(((ApplyEffectAction)root.activationActions[0]).effect));Assert.IsFalse(System.IO.File.Exists(modulePath));
                Undo.PerformRedo();Assert.AreEqual(guid,AssetDatabase.AssetPathToGUID(modulePath));Check(Outer().actions[0],modulePath);
                Undo.PerformRedo();var inner=((CallModuleAction)Outer().actions[0]).module;
                Assert.AreEqual(innerGuid,AssetDatabase.AssetPathToGUID(innerPath));Check(inner.actions[0],innerPath);
                Assert.IsEmpty(System.Array.FindAll(AssetDatabase.LoadAllAssetsAtPath(modulePath),a=>a is GameplayEffectAsset),"Redo left an orphan in the outer module.");
            }
        }
        [TearDown] public void Cleanup(){AssetDatabase.DeleteAsset(rootPath);AssetDatabase.DeleteAsset(modulePath);AssetDatabase.DeleteAsset(innerPath);}
    }
    public sealed class FailedModuleUndoReloadTests
    {
        [SerializeField] private string ownerPath,modulePath,backup,actionId;
        [SerializeField] private int childBeforeReload,parentBeforeReload;
        [UnitySetUp] public IEnumerator FailBeforeReload(){
            ownerPath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASFailedReloadOwner.asset");
            modulePath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASFailedReloadModule.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,ownerPath);
            var fx=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"reload private");
            var child=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"reload child");
            using(var edit=new SerializedObject(fx)){var children=edit.FindProperty("conditionalEffects");children.arraySize=1;
                children.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue=child;edit.ApplyModifiedPropertiesWithoutUndo();}
            root.activationActions=new SkillAction[]{new ApplyEffectAction{effect=fx}};actionId=root.activationActions[0].Id;
            SkillFlowCommands.Extract(null,root,"activationActions",0,1,modulePath,_=>true);
            Undo.PerformUndo();var entry=ModuleFileJournal.instance.entries.Single(e=>e.path==modulePath);
            entry.path="Assets/"+System.Guid.NewGuid().ToString("N")+"/"+Path.GetFileName(modulePath);
            LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("DirectoryNotFoundException:"));
            Undo.PerformRedo();backup=entry.recovery;Assert.IsTrue(File.Exists(backup));var repaired=Check();
            parentBeforeReload=repaired.GetInstanceID();childBeforeReload=Child(repaired).GetInstanceID();
            Undo.PerformUndo();Check();Assert.IsFalse(EditorUtility.IsPersistent(repaired));
            EditorUtility.RequestScriptReload();yield return new WaitForDomainReload();
        }
        private static GameplayEffectAsset Child(GameplayEffectAsset fx)=>AssetReferences.Children(fx).OfType<GameplayEffectAsset>().Single();
        private GameplayEffectAsset Check(){
            var root=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(ownerPath);var inline=root.activationActions[0] as ApplyEffectAction;
            Assert.IsNotNull(inline);Assert.AreEqual(actionId,inline.Id);Assert.AreEqual("reload private",inline.effect.name);
            var child=Child(inline.effect);Assert.AreEqual("reload child",child.name);
            CollectionAssert.AreEquivalent(new[]{inline.effect,child},AssetDatabase.LoadAllAssetsAtPath(ownerPath).OfType<GameplayEffectAsset>().ToArray());
            return inline.effect;
        }
        [Test] public void FailedPublicationStaysInlineAcrossReloadAndHistoryCycles(){
            Check();Undo.PerformRedo();var repaired=Check();
            Assert.AreEqual(parentBeforeReload,repaired.GetInstanceID());Assert.AreEqual(childBeforeReload,Child(repaired).GetInstanceID());
            Undo.PerformUndo();Check();Undo.PerformRedo();Check();
            AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(ownerPath));
            AssetDatabase.ImportAsset(ownerPath,ImportAssetOptions.ForceSynchronousImport);Check();
        }
        [TearDown] public void Cleanup(){
            if(!string.IsNullOrEmpty(backup))ModuleFileUndo.ClearRecoveryDirectory(Path.GetDirectoryName(backup));
            AssetDatabase.DeleteAsset(ownerPath);AssetDatabase.DeleteAsset(modulePath);
        }
    }

}
