using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class ModuleEditingTests
    {
        private const string Folder="Assets/GASSimpleModuleTests";
        private GameplayAbilityAsset _skill;
        [SetUp] public void Setup() {
            AssetDatabase.CreateFolder("Assets","GASSimpleModuleTests");
            _skill=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(_skill,Folder+"/Skill.asset");
        }
        [TearDown] public void Cleanup(){Undo.ClearAll();AssetDatabase.DeleteAsset(Folder);}
        [Test] public void ExtractMovesAnExclusiveTreeAndCannotBeUndone() {
            var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill,"Hit");
            var child=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill,"Child");
            using(var so=new SerializedObject(effect)){var a=so.FindProperty("conditionalEffects");a.arraySize=1;a.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue=child;so.ApplyModifiedPropertiesWithoutUndo();}
            var action=new ApplyEffectAction{effect=effect};_skill.activationActions=new SkillAction[]{action};
            using var doc=new SkillDocument();doc.Open(_skill);
            string path=Folder+"/Module.asset";
            SkillFlowCommands.Extract(doc,_skill,"activationActions",0,1,path,message=>{StringAssert.Contains("不可撤销",message);return true;});
            var module=((CallModuleAction)_skill.activationActions[0]).module;
            Assert.AreSame(effect,((ApplyEffectAction)module.actions[0]).effect);Assert.AreEqual(action.Id,module.actions[0].Id);
            Assert.AreEqual(path,AssetDatabase.GetAssetPath(effect));Assert.AreEqual(path,AssetDatabase.GetAssetPath(child));
            Assert.IsEmpty(AssetDatabase.LoadAllAssetsAtPath(Folder+"/Skill.asset").OfType<GameplayEffectAsset>());
            Undo.PerformUndo();Assert.AreSame(module,((CallModuleAction)_skill.activationActions[0]).module);
            var denied=Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Inline(doc,_skill,"activationActions",0,_=>true));
            StringAssert.Contains("外置",denied.Message);Assert.AreSame(module,((CallModuleAction)_skill.activationActions[0]).module);
        }
        [Test] public void SharedPrivateEffectRefusesExtractionBeforeWriting() {
            var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill,"Shared");
            _skill.activationActions=new SkillAction[]{new ApplyEffectAction{effect=effect},new ApplyEffectAction{effect=effect}};
            string path=Folder+"/Module.asset";
            StringAssert.Contains("共用",Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(null,_skill,"activationActions",0,1,path,_=>true)).Message);
            Assert.IsFalse(System.IO.File.Exists(path));Assert.AreEqual(2,_skill.activationActions.Length);
        }
        [Test] public void ModuleReferenceProtectsPrivateEffectFromMoving() {
            var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill,"Shared");
            _skill.activationActions=new SkillAction[]{new ApplyEffectAction{effect=effect}};
            var other=ScriptableObject.CreateInstance<SkillActionModuleAsset>();other.actions=new SkillAction[]{new ApplyEffectAction{effect=effect}};
            AssetDatabase.CreateAsset(other,Folder+"/Other.asset");AssetDatabase.SaveAssets();
            StringAssert.Contains("Other.asset",Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(null,_skill,"activationActions",0,1,Folder+"/Module.asset",_=>true)).Message);
        }
        [Test] public void PasteRejectsPrivateEffectsButReusesStandaloneIdentity() {
            var other=ScriptableObject.CreateInstance<SkillActionModuleAsset>();AssetDatabase.CreateAsset(other,Folder+"/Other.asset");
            var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill,"Hit");
            using var so=new SerializedObject(other);var list=so.FindProperty("actions");
            SkillActionClipboard.Copy(new ApplyEffectAction{effect=effect});
            Assert.Throws<InvalidOperationException>(()=>SkillActionClipboard.Paste(list,0));Assert.AreEqual(0,list.arraySize);
            AssetDatabase.RemoveObjectFromAsset(effect);AssetDatabase.CreateAsset(effect,Folder+"/Shared.asset");
            SkillActionClipboard.Copy(new ApplyEffectAction{effect=effect});SkillActionClipboard.Paste(list,0);SkillActionClipboard.Paste(list,1);so.ApplyModifiedProperties();
            Assert.AreSame(effect,((ApplyEffectAction)other.actions[0]).effect);Assert.AreSame(effect,((ApplyEffectAction)other.actions[1]).effect);
        }
        [Test] public void ExtractAndInlinePreserveParametersAndConfirmBeforeMutation() {
            var parameter=new SkillParameter{displayName="Count",defaultValue=3};_skill.parameters=new[]{parameter};
            var action=new SpawnPrefabAction{count=new SkillNumber(1){parameterId=parameter.Id}};_skill.activationActions=new SkillAction[]{action};
            string path=Folder+"/Module.asset";
            Assert.Throws<OperationCanceledException>(()=>SkillFlowCommands.Extract(null,_skill,"activationActions",0,1,path,_=>false));
            Assert.IsFalse(System.IO.File.Exists(path));Assert.AreSame(action,_skill.activationActions[0]);
            SkillFlowCommands.Extract(null,_skill,"activationActions",0,1,path,_=>true);
            var call=(CallModuleAction)_skill.activationActions[0];Assert.AreEqual(parameter.Id,call.bindings[0].callerParameterId);
            Assert.AreEqual(3,call.module.parameters.parameters[0].defaultValue);
            SkillFlowCommands.Inline(null,_skill,"activationActions",0,message=>{StringAssert.Contains("不可撤销",message);return true;});
            Assert.AreEqual(parameter.Id,((SpawnPrefabAction)_skill.activationActions[0]).count.parameterId);
            Assert.AreNotEqual(action.Id,_skill.activationActions[0].Id);
        }
    }
}
