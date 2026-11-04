using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class ImprovementPlanTests
    {
        [Test] public void BlendPresetsHaveBoundedWeightsAndDefinitionsKeepAuthoredCurve()
        {
            foreach(SkillBlendCurve curve in Enum.GetValues(typeof(SkillBlendCurve)))
            {
                var blend=new SkillBlend(.2f,curve,custom:AnimationCurve.Linear(0,0,1,1));
                Assert.AreEqual(0,blend.Evaluate(0));Assert.AreEqual(1,blend.Evaluate(1));
                for(int i=0;i<=100;i++) Assert.That(blend.Evaluate(i/100f),Is.InRange(0f,1f));
            }
            var custom=AnimationCurve.Linear(0,0,1,1);var spec=new SkillAnimationDefinition("a",1,blendInSpec:new SkillBlend(.5f,SkillBlendCurve.Custom,custom:custom));
            custom.keys=AnimationCurve.Constant(0,1,0).keys;Assert.AreEqual(.5f,spec.BlendInSpec.Evaluate(.5f),.001f);
            Assert.Throws<ArgumentOutOfRangeException>(()=>new AnimationSectionDefinition("a","a",0,.25f,null,0,.25f,new SkillBlend(.3f)));
        }
        private sealed class SetChannel : SkillActionDefinition
        { public SetChannel():base("set","set"){} public override void Validate(SkillExecutionContext c,bool deferred=false){}
          public override void Execute(SkillExecutionContext c)=>c.SetChannel("proof",2); }
        [Test] public void ThrowingObserversCannotSplitEffectsOrSkipActionsOrOtherObservers()
        {
            TestTags.Initialize();var source=new AbilitySystemComponent();int applied=0,finished=0,failedEffect=0,failedAction=0;
            var tag=GameplayTags.GameplayTagManager.RequestTag("Test.Window");
            var effect=new GameplayEffectDefinition(EffectDurationPolicy.Duration,1,tags:new EffectTags(granted:new GameplayTagSet("Test.Window")));
            var effectError=new ArgumentException("test effect observer");var actionError=new ArgumentException("test action observer");
            Exception recordedEffect=null,recordedAction=null;
            GasDebugHooks.EffectHook bad=(_,__,___,____)=>{failedEffect++;throw effectError;},good=(_,__,___,____)=>{applied++;recordedEffect=DebugObserverErrors.Last;};
            Action<SkillExecutionContext,SkillActionDefinition> fail=(_,__)=>{failedAction++;throw actionError;};
            Action<SkillExecutionContext,SkillActionDefinition> observe=(_,__)=>recordedAction=DebugObserverErrors.Last;
            Action<SkillExecutionContext,SkillActionDefinition,Exception> end=(_,__,err)=>{Assert.IsNull(err);finished++;};
            GasDebugHooks.Applied+=bad;GasDebugHooks.Applied+=good;SkillActionDebug.Boundary+=fail;SkillActionDebug.Boundary+=observe;SkillActionDebug.ActionFinished+=end;
            try{
                source.Effects.Apply(new GameplayEffectSpec(effect,source.Attributes,source));
                Assert.AreEqual(1,source.OwnedTags.GetTagCount(tag));Assert.AreEqual(1,applied);
                var spec=source.GrantAbility(new GameplayAbilityDefinition());source.TryActivateAbility(spec);
                var c=new SkillExecutionContext(null,spec,null,new EffectEvaluationContext(1),null);
                Assert.IsTrue(c.ExecuteAction(new SetChannel()));Assert.AreEqual(2,c.Channels["proof"]);Assert.AreEqual(1,finished);
                source.Tick(1.1f);Assert.AreEqual(0,source.OwnedTags.GetTagCount(tag));Assert.AreEqual(1,failedEffect);Assert.AreEqual(1,failedAction);Assert.AreSame(effectError,recordedEffect);Assert.AreSame(actionError,recordedAction);
            }finally{GasDebugHooks.Applied-=bad;GasDebugHooks.Applied-=good;SkillActionDebug.Boundary-=fail;SkillActionDebug.Boundary-=observe;SkillActionDebug.ActionFinished-=end;source.Clear();}
        }
        [Test] public void EasingPresetsHaveDistinctInteriorWeights()
        {
            Assert.AreEqual(.125f,new SkillBlend(1,SkillBlendCurve.QuadInOut).Evaluate(.25f),1e-5f);
            Assert.AreEqual(.875f,new SkillBlend(1,SkillBlendCurve.CubicOut).Evaluate(.5f),1e-5f);
            Assert.AreEqual(.5f,new SkillBlend(1,SkillBlendCurve.ExpoInOut).Evaluate(.5f),1e-5f);
            Assert.Less(new SkillBlend(1,SkillBlendCurve.BackIn).Evaluate(.5f),.1f);
        }
        [Test] public void MovingAcrossBranchesKeepsIdsAndUndoRestoresOriginalOrder()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            try
            {
                var first=new ConditionalAction();var second=new ApplyEffectAction();
                root.activationActions=new SkillAction[]{first,second};
                string prefix="activationActions.Array.data[0]";
                SkillFlowCommands.Move(null,root,"activationActions.Array.data[1]",prefix+".whenFalse",-1);
                Assert.AreEqual(1,root.activationActions.Length);
                Assert.AreEqual(second.Id,((ConditionalAction)root.activationActions[0]).whenFalse[0].Id);
                Undo.PerformUndo(); Assert.AreEqual(2,root.activationActions.Length);Assert.AreEqual(second.Id,root.activationActions[1].Id);
                Undo.PerformRedo(); Assert.AreEqual(1,root.activationActions.Length);
                using var so=new SerializedObject(root);SkillActionClipboard.Copy(root.activationActions[0]);
                SkillActionClipboard.Paste(so.FindProperty("activationActions"),1);so.ApplyModifiedProperties();
                var copy=(ConditionalAction)root.activationActions[1]; Assert.AreNotEqual(first.Id,copy.Id); Assert.AreNotEqual(second.Id,copy.whenFalse[0].Id);
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void ExtractAndInlineBindParametersAndUndoInOneStep()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            SkillEditorWindow window=null;
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASFlowExtractTest.asset");
            var modulePath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASFlowExtractModule.asset");
            AssetDatabase.CreateAsset(root,path);
            try
            {
                var p=new SkillParameter{displayName="伤害",defaultValue=3};root.parameters=new[]{p};
                var action=new SpawnPrefabAction{count=new SkillNumber(1){parameterId=p.Id}};root.activationActions=new SkillAction[]{action,new ConditionalAction()};
                using var doc=new SkillDocument();doc.Open(root);
                SkillFlowCommands.Extract(doc,root,"activationActions",0,2,modulePath,_=>true);
                var call=(CallModuleAction)root.activationActions[0];Assert.AreEqual(1,call.bindings.Length);Assert.AreEqual(p.Id,call.bindings[0].callerParameterId);
                Assert.AreEqual(2,call.module.actions.Length);Assert.AreEqual(3,call.module.parameters.parameters[0].defaultValue);
                window=ScriptableObject.CreateInstance<SkillEditorWindow>();window.ShowUtility();window.Show(root);
                window.Document.Selection.Select(call.module);window.Document.OpenFlow();
                Undo.PerformUndo();Assert.AreEqual(2,root.activationActions.Length);Assert.AreEqual(action.Id,root.activationActions[0].Id);
                Assert.IsFalse(System.IO.File.Exists(modulePath),"Undo must remove the extracted file.");
                Undo.PerformRedo();call=(CallModuleAction)root.activationActions[0];Assert.IsNotNull(call.module);
                Assert.AreEqual(modulePath,AssetDatabase.GetAssetPath(call.module),"Redo must restore the independent module asset rather than embed it in the caller.");
                Assert.AreSame(call.module,AssetDatabase.LoadMainAssetAtPath(modulePath));
                Assert.AreEqual(modulePath,AssetDatabase.GetAssetPath(call.module.parameters));
                Assert.AreEqual(3,call.module.parameters.parameters[0].defaultValue);
                SkillFlowCommands.Inline(doc,root,"activationActions",0,_=>true);
                Assert.AreEqual(p.Id,((SpawnPrefabAction)root.activationActions[0]).count.parameterId);Assert.AreNotEqual(action.Id,root.activationActions[0].Id);
                Undo.PerformUndo();Assert.IsInstanceOf<CallModuleAction>(root.activationActions[0]);
            }
            finally{window?.Close();AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(modulePath);}
        }
        [Test] public void NestedExtractionRestoresBothFilesReferencesAndActionIds()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            string PathFor(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASNested"+n+".asset");
            var path=PathFor("Root");var outer=PathFor("Outer");var inner=PathFor("Inner");string relocated=null;
            var a=new ConditionalAction();var b=new ApplyEffectAction();root.activationActions=new SkillAction[]{a,b};
            AssetDatabase.CreateAsset(root,path);b.effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"private");
            try{
                using var doc=new SkillDocument();doc.Open(root);
                SkillFlowCommands.Extract(doc,root,"activationActions",0,2,outer,_=>true);
                SkillActionModuleAsset Outer()=>((CallModuleAction)root.activationActions[0]).module;
                SkillFlowCommands.Extract(doc,Outer(),"actions",1,1,inner,_=>true);
                var guid1=AssetDatabase.AssetPathToGUID(outer);var guid2=AssetDatabase.AssetPathToGUID(inner);
                var callId=((CallModuleAction)Outer().actions[1]).Id;
                for(int cycle=0;cycle<2;cycle++){
                    Undo.PerformUndo();Assert.IsFalse(System.IO.File.Exists(inner));Assert.AreEqual(b.Id,Outer().actions[1].Id);
                    Assert.AreEqual(outer,AssetDatabase.GetAssetPath(((ApplyEffectAction)Outer().actions[1]).effect));
                    Undo.PerformUndo();Assert.IsFalse(System.IO.File.Exists(outer));Assert.AreEqual(a.Id,root.activationActions[0].Id);
                    if(cycle==1)AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<SkillActionModuleAsset>(),outer);
                    Undo.PerformRedo();relocated=AssetDatabase.GetAssetPath(Outer());
                    if(cycle==0)Assert.AreEqual(outer,relocated);else Assert.AreNotEqual(outer,relocated);
                    Assert.AreEqual(b.Id,Outer().actions[1].Id);
                    Undo.PerformRedo();var call=(CallModuleAction)Outer().actions[1];
                    Assert.AreEqual(callId,call.Id);Assert.AreEqual(inner,AssetDatabase.GetAssetPath(call.module));Assert.AreEqual(b.Id,call.module.actions[0].Id);
                    Assert.AreEqual(inner,AssetDatabase.GetAssetPath(((ApplyEffectAction)call.module.actions[0]).effect));
                    if(cycle==0)Assert.AreEqual(guid1,AssetDatabase.AssetPathToGUID(outer));Assert.AreEqual(guid2,AssetDatabase.AssetPathToGUID(inner));}
            }finally{AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(outer);AssetDatabase.DeleteAsset(inner);if(relocated!=null && relocated!=outer)AssetDatabase.DeleteAsset(relocated);}
        }
        [Test] public void ExtractedEffectsAreIndependentAndOldModuleReferencesProtectOriginals()
        {
            string PathFor(string suffix)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASModuleOwnership"+suffix+".asset");
            var path=PathFor("Root");var sharedPath=PathFor("Shared");var mp=PathFor("Module");var legacy=PathFor("Legacy");var duplicate=PathFor("Copy");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,path);
            try{
                var first=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"first");var child=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"child");
                var shared=ScriptableObject.CreateInstance<GameplayEffectAsset>();AssetDatabase.CreateAsset(shared,sharedPath);
                using(var so=new SerializedObject(first)){
                    var children=so.FindProperty("conditionalEffects");children.arraySize=2;
                    children.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue=child;
                    children.GetArrayElementAtIndex(1).FindPropertyRelative("effect").objectReferenceValue=shared;so.ApplyModifiedPropertiesWithoutUndo();}
                root.activationActions=new SkillAction[]{new ApplyEffectAction{effect=first},new ApplyEffectAction{effect=first},new ApplyEffectAction{effect=first}};
                EditorUtility.SetDirty(root);AssetDatabase.SaveAssetIfDirty(root);
                using var doc=new SkillDocument();doc.Open(root);
                var conflict=Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(doc,root,"activationActions",0,2,mp));
                StringAssert.Contains("first",conflict.Message);Assert.IsFalse(System.IO.File.Exists(mp));
                foreach(var a in root.activationActions)Assert.AreSame(first,((ApplyEffectAction)a).effect);
                var previous=ScriptableObject.CreateInstance<SkillActionModuleAsset>();previous.actions=new SkillAction[]{new ApplyEffectAction{effect=child}};
                AssetDatabase.CreateAsset(previous,legacy);SkillAssetFactory.DeleteNode(doc,child);Assert.IsTrue(child!=null);
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(doc,root,"activationActions",0,3,mp));
                AssetDatabase.DeleteAsset(legacy);
                doc.Layout.SetPosition(first,new Vector2(72,140));doc.Layout.Save();
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(doc,root,"activationActions",0,3,mp));
                SkillFlowCommands.Extract(doc,root,"activationActions",0,3,mp,_=>true);
                var module=((CallModuleAction)root.activationActions[0]).module;
                var copy=((ApplyEffectAction)module.actions[0]).effect;Assert.AreEqual(new Vector2(72,140),doc.Layout.Position(copy,Vector2.zero));Assert.AreSame(copy,((ApplyEffectAction)module.actions[1]).effect);
                Assert.IsEmpty(AssetDatabase.LoadAllAssetsAtPath(path).OfType<GameplayEffectAsset>());
                var refs=AssetReferences.Children(copy).ToArray();Assert.AreEqual(mp,AssetDatabase.GetAssetPath(refs[0]));Assert.AreSame(shared,refs[1]);
                AssetDatabase.SaveAssetIfDirty(root);
                var copied=SkillAssetFactory.Duplicate(root,duplicate);
                Assert.AreSame(module,((CallModuleAction)copied.activationActions[0]).module);
                CollectionAssert.DoesNotContain(AssetDatabase.GetDependencies(duplicate),path);
                Undo.PerformUndo();Assert.IsTrue(System.IO.File.Exists(mp),"A duplicate outside the Undo stack still needs this file.");
                Assert.AreSame(module,((CallModuleAction)copied.activationActions[0]).module);
                Assert.AreEqual(path,AssetDatabase.GetAssetPath(((ApplyEffectAction)root.activationActions[0]).effect));
                Undo.PerformRedo();Assert.AreSame(module,((CallModuleAction)root.activationActions[0]).module);
            }finally{foreach(string p in new[]{duplicate,legacy,path,mp,sharedPath})AssetDatabase.DeleteAsset(p);}
        }
        [TestCase(false),TestCase(true)] public void ClipboardLocalizesNestedPrivateEffectsAndRedoUsesFreePath(bool moveOccupant)
        {
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASOwnership"+n+".asset");
            string path=P("Source"),mp=P("Module"),bp=P("Target"),restored=null;
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,path);
            var target=ScriptableObject.CreateInstance<SkillActionModuleAsset>();AssetDatabase.CreateAsset(target,bp);
            try{
                var x=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"private");
                var c=new ConditionalAction{whenTrue=new SkillAction[]{new ApplyEffectAction{effect=x}},whenFalse=new SkillAction[]{new ApplyEffectAction{effect=x}}};
                root.activationActions=new SkillAction[]{c};
                SkillActionClipboard.Copy(c);using(var dst=new SerializedObject(target)){
                    var actions=dst.FindProperty("actions");
                    Assert.Throws<InvalidOperationException>(()=>SkillActionClipboard.Paste(actions,0));Assert.AreEqual(0,actions.arraySize);
                    SkillActionClipboard.Paste(actions,0,_=>true);dst.ApplyModifiedProperties();
                    // A different source action using the same effect should find the same persistent copy.
                    SkillActionClipboard.Copy(new ApplyEffectAction{effect=x});dst.Update();
                    SkillActionClipboard.Paste(dst.FindProperty("actions"),1);dst.ApplyModifiedProperties();}
                var copied=(ConditionalAction)target.actions[0];var owned=((ApplyEffectAction)copied.whenTrue[0]).effect;
                Assert.AreEqual(bp,AssetDatabase.GetAssetPath(owned));Assert.AreSame(owned,((ApplyEffectAction)copied.whenFalse[0]).effect);Assert.AreSame(owned,((ApplyEffectAction)target.actions[1]).effect);
                using var doc=new SkillDocument();doc.Open(root);SkillFlowCommands.Extract(doc,root,"activationActions",0,1,mp,_=>true);
                string guid=AssetDatabase.AssetPathToGUID(mp);Undo.PerformUndo();
                var unrelated=ScriptableObject.CreateInstance<SkillActionModuleAsset>();string free=P("Occupant");
                AssetDatabase.CreateAsset(unrelated,moveOccupant?free:mp);
                if(moveOccupant)Assert.AreEqual("",AssetDatabase.MoveAsset(free,mp));
                Undo.PerformRedo();restored=AssetDatabase.GetAssetPath(((CallModuleAction)root.activationActions[0]).module);
                Assert.IsNotEmpty(restored);
                Assert.AreNotEqual(mp,restored);Assert.AreSame(unrelated,AssetDatabase.LoadMainAssetAtPath(mp));
                var restoredModule=((CallModuleAction)root.activationActions[0]).module;
                if(moveOccupant)Assert.AreEqual(guid,AssetDatabase.AssetPathToGUID(restored));
                var action=(ConditionalAction)restoredModule.actions[0];
                var effect=((ApplyEffectAction)action.whenTrue[0]).effect;
                Assert.IsNotNull(effect);Assert.AreEqual(restored,AssetDatabase.GetAssetPath(effect));
                Assert.AreSame(effect,((ApplyEffectAction)action.whenFalse[0]).effect);
                Undo.ClearAll();ModuleFileUndo.Prune();Assert.IsFalse(ModuleFileJournal.instance.entries.Any(e=>e.guid==AssetDatabase.AssetPathToGUID(restored)));
            }finally{foreach(var p in new[]{path,bp,mp,restored})if(!string.IsNullOrEmpty(p))AssetDatabase.DeleteAsset(p);}
        }
        [Test] public void InlineWithoutParametersOwnsItsEffectsAfterUndoRedoAndDeletingModule()
        {
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASPlainInline.asset");
            var mp=AssetDatabase.GenerateUniqueAssetPath("Assets/GASPlainInlineModule.asset");
            var skill=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                AssetDatabase.CreateAsset(module,mp);var effect=ScriptableObject.CreateInstance<GameplayEffectAsset>();effect.name="private";AssetDatabase.AddObjectToAsset(effect,module);
                module.actions=new SkillAction[]{new ApplyEffectAction{effect=effect},new ApplyEffectAction{effect=effect}};
                skill.activationActions=new SkillAction[]{new CallModuleAction{module=module}};
                AssetDatabase.CreateAsset(skill,path);
                using var doc=new SkillDocument();doc.Open(skill);
                var denied=Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Inline(doc,skill,"activationActions",0));
                StringAssert.Contains("叠层",denied.Message);Assert.AreSame(module,((CallModuleAction)skill.activationActions[0]).module);
                Assert.IsEmpty(AssetDatabase.LoadAllAssetsAtPath(path).OfType<GameplayEffectAsset>());
                SkillFlowCommands.Inline(doc,skill,"activationActions",0,_=>true);
                void AssertLocal(){
                    var first=((ApplyEffectAction)skill.activationActions[0]).effect;Assert.IsNotNull(first);
                    Assert.AreEqual(path,AssetDatabase.GetAssetPath(first));Assert.AreSame(first,((ApplyEffectAction)skill.activationActions[1]).effect);
                }
                AssertLocal();Undo.PerformUndo();Assert.AreSame(module,((CallModuleAction)skill.activationActions[0]).module);
                Undo.PerformRedo();AssertLocal();
                AssetDatabase.SaveAssetIfDirty(skill);AssetDatabase.DeleteAsset(mp);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate);
                skill=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(path);AssertLocal();
            }finally{AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(mp);}
        }
        [Test] public void InliningASecondSharedCallNeverSilentlySwitchesItsEffectIdentity()
        {
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASInlineShared"+n+".asset");
            string ap=P("A"),bp=P("B"),mp=P("Module");
            var a=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var b=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            var m=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                AssetDatabase.CreateAsset(m,mp);var burn=ScriptableObject.CreateInstance<GameplayEffectAsset>();AssetDatabase.AddObjectToAsset(burn,m);
                m.actions=new SkillAction[]{new ApplyEffectAction{effect=burn}};
                a.activationActions=new SkillAction[]{new CallModuleAction{module=m},new CallModuleAction{module=m},new CallModuleAction{module=m}};
                b.activationActions=new SkillAction[]{new CallModuleAction{module=m}};
                AssetDatabase.CreateAsset(a,ap);AssetDatabase.CreateAsset(b,bp);using var doc=new SkillDocument();doc.Open(a);
                SkillFlowCommands.Inline(doc,a,"activationActions",0,_=>true);var copy=((ApplyEffectAction)a.activationActions[0]).effect;
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Inline(doc,a,"activationActions",1));
                Assert.AreSame(m,((CallModuleAction)a.activationActions[1]).module);
                SkillFlowCommands.Inline(doc,a,"activationActions",1,_=>true);
                Assert.AreSame(copy,((ApplyEffectAction)a.activationActions[1]).effect);
                Assert.AreSame(m,((CallModuleAction)b.activationActions[0]).module);Assert.AreSame(burn,((ApplyEffectAction)m.actions[0]).effect);
                Assert.AreNotSame(burn,copy);
                using(var edit=new SerializedObject(copy)){edit.FindProperty("duration").floatValue=9;edit.ApplyModifiedPropertiesWithoutUndo();}
                Assert.Throws<OperationCanceledException>(()=>SkillFlowCommands.Inline(doc,a,"activationActions",2,_=>true,_=>ActionEffectOwnership.CopyDecision.Cancel));
                Assert.AreSame(m,((CallModuleAction)a.activationActions[2]).module);
                SkillFlowCommands.Inline(doc,a,"activationActions",2,_=>true,_=>ActionEffectOwnership.CopyDecision.New);
                var newer=((ApplyEffectAction)a.activationActions[2]).effect;
                Assert.AreNotSame(copy,newer);Assert.AreEqual(burn.Definition.Duration,newer.Definition.Duration);Assert.AreEqual(9,copy.Definition.Duration);
            }finally{foreach(string p in new[]{ap,bp,mp})AssetDatabase.DeleteAsset(p);}
        }
        [TestCase("prefab"),TestCase("unity"),TestCase("asset"),TestCase("unmarked"),TestCase("gasrefcheck"),TestCase("preset"),TestCase("signal"),TestCase("mixer")]
        public void ExternalReferencesBlockExtractionBeforeConsentOrPublication(string extension)
        {
            string rootPath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectGuardRoot.asset"),
                external=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectGuardUser."+(extension=="unmarked"?"asset":extension)),
                module=AssetDatabase.GenerateUniqueAssetPath("Assets/GASEffectGuardModule.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,rootPath);
            try{
                var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"Burn");
                root.activationActions=new SkillAction[]{new ApplyEffectAction{effect=effect}};
                EditorUtility.SetDirty(root);AssetDatabase.SaveAssetIfDirty(root);
                if(extension=="asset" || extension=="unmarked"){var custom=ScriptableObject.CreateInstance<EffectReferenceFixture>();
                    if(extension=="asset")custom.effect=effect;AssetDatabase.CreateAsset(custom,external);
                    if(extension=="unmarked"){SkillReferenceGuard.Prepare();custom.effect=effect;}}
                else if(extension is "signal" or "mixer" or "preset"){
                    var reference=ScriptableObject.CreateInstance<EffectReferenceFixture>();reference.effect=effect;
                    if(extension=="preset"){
                        try{AssetDatabase.CreateAsset(new UnityEditor.Presets.Preset(reference),external);}
                        finally{UnityEngine.Object.DestroyImmediate(reference);}
                    }else{
                        // The mixer is a real mixer; signal containers may be used without Timeline installed.
                        if(extension=="mixer"){
                            var create=typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Audio.AudioMixerController")
                                .GetMethod("CreateMixerControllerAtPath",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
                            Assert.IsNotNull(create);create.Invoke(null,new object[]{external});
                            AssetDatabase.AddObjectToAsset(reference,external);
                        }else AssetDatabase.CreateAsset(reference,external);
                        EditorUtility.SetDirty(reference);AssetDatabase.SaveAssets();
                        Resources.UnloadAsset(reference);
                    }
                    AssetDatabase.ImportAsset(external,ImportAssetOptions.ForceSynchronousImport);
                    CollectionAssert.Contains(AssetDatabase.GetDependencies(external,false),rootPath);
                }
                else if(extension=="gasrefcheck"){
                    System.IO.File.WriteAllText(external,rootPath);
                    AssetDatabase.ImportAsset(external,ImportAssetOptions.ForceSynchronousImport);
                }else{
                    bool prefab=extension=="prefab";
                    if(!prefab){
                        // Open an existing empty additive fixture; NewScene(Additive) rejects
                        // an untitled editor scene even when it belongs to the test runner.
                        System.IO.File.WriteAllText(external,"%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots: []\n");
                        AssetDatabase.ImportAsset(external,ImportAssetOptions.ForceSynchronousImport);
                    }
                    var scene=prefab?UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene():
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(external,UnityEditor.SceneManagement.OpenSceneMode.Additive);
                    try{var go=new GameObject("Outside GAS");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,scene);
                        go.AddComponent<UnityEngine.Playables.PlayableDirector>().SetReferenceValue("burn",effect);
                        if(extension=="unity")Assert.IsTrue(UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene));
                        else PrefabUtility.SaveAsPrefabAsset(go,external);
                    }finally{if(prefab)UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                        else UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene,true);}
                }
                bool asked=false;string key=AssetReferences.Key(effect);
                using var doc=new SkillDocument();doc.Open(root);
                var error=Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(doc,root,"activationActions",0,1,module,_=>{asked=true;return true;}));
                StringAssert.Contains(external,error.Message);Assert.IsFalse(asked);Assert.IsFalse(System.IO.File.Exists(module));
                Assert.AreSame(effect,((ApplyEffectAction)root.activationActions[0]).effect);Assert.AreEqual(key,AssetReferences.Key(effect));
            }finally{foreach(var p in new[]{external,module,rootPath})AssetDatabase.DeleteAsset(p);}
        }
        [Test] public void ReferenceToTheSkillFileIsNotAReferenceToItsPrivateEffect()
        {
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASHostOnly"+n+".asset");
            string rootPath=P("Skill"),external=P("User"),module=P("Module");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,rootPath);
            try{
                var effect=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"Burn");
                root.activationActions=new SkillAction[]{new ApplyEffectAction{effect=effect}};
                EditorUtility.SetDirty(root);AssetDatabase.SaveAssetIfDirty(root);
                var custom=ScriptableObject.CreateInstance<EffectReferenceFixture>();custom.skill=root;
                AssetDatabase.CreateAsset(custom,external);
                using var doc=new SkillDocument();doc.Open(root);
                string originalKey=AssetReferences.Key(effect);
                SkillFlowCommands.Extract(doc,root,"activationActions",0,1,module,msg=>{StringAssert.Contains(originalKey,msg);return true;});
                var extracted=((CallModuleAction)root.activationActions[0]).module;
                Assert.AreEqual(module,AssetDatabase.GetAssetPath(((ApplyEffectAction)extracted.actions[0]).effect));
                Assert.AreSame(root,custom.skill);
            }finally{foreach(string p in new[]{external,module,rootPath})AssetDatabase.DeleteAsset(p);}
        }
        [Test] public void ChangedSourceNeedsAnExplicitVersionAndNeverRewritesOlderActions()
        {
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASVersionedCopy"+n+".asset");
            string ap=P("Source"),bp=P("Target");
            var a=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var b=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                AssetDatabase.CreateAsset(a,ap);AssetDatabase.CreateAsset(b,bp);var x=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(a,"Burn");
                void Duration(float n){using var src=new SerializedObject(x);src.FindProperty("duration").floatValue=n;src.ApplyModifiedPropertiesWithoutUndo();}
                GameplayEffectAsset Paste(Func<string,ActionEffectOwnership.CopyDecision> conflict=null){
                    using var dst=new SerializedObject(b);var list=dst.FindProperty("actions");
                    SkillActionClipboard.Copy(new ApplyEffectAction{effect=x});SkillActionClipboard.Paste(list,list.arraySize,
                        msg=>{StringAssert.Contains(AssetReferences.Key(x),msg);return true;},conflict);
                    dst.ApplyModifiedProperties();return ((ApplyEffectAction)b.actions[b.actions.Length-1]).effect;}
                Duration(2);var old=Paste();Duration(6);
                Assert.Throws<OperationCanceledException>(()=>Paste(_=>ActionEffectOwnership.CopyDecision.Cancel));
                Assert.AreEqual(1,b.actions.Length);
                Assert.AreSame(old,Paste(msg=>{StringAssert.Contains(AssetReferences.Key(x),msg);return ActionEffectOwnership.CopyDecision.Reuse;}));
                Assert.AreSame(old,Paste(_=>throw new Exception("Accepted source and destination should not prompt again.")));
                Duration(7);var newer=Paste(_=>ActionEffectOwnership.CopyDecision.New);Assert.AreNotSame(old,newer);
                Assert.AreEqual(2,old.Definition.Duration);Assert.AreEqual(7,newer.Definition.Duration);
                Assert.AreSame(newer,Paste(_=>throw new Exception("the matching version needs no decision")));
                Duration(2);Assert.AreSame(old,Paste(_=>throw new Exception("reverting source values should find the first version")));
                if(Application.isBatchMode){Assert.IsFalse(ActionEffectOwnership.ConfirmCopy("headless"));
                    Assert.IsFalse(ActionEffectOwnership.ConfirmMove("headless"));}
            }finally{AssetDatabase.DeleteAsset(ap);AssetDatabase.DeleteAsset(bp);}
        }
        [Test] public void ChangedDestinationAndNestedCopiesNeedOnlyOneDecision(){
            string src=AssetDatabase.GenerateUniqueAssetPath("Assets/GASLocalHashSource.asset");
            string dst=AssetDatabase.GenerateUniqueAssetPath("Assets/GASLocalHashTarget.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                AssetDatabase.CreateAsset(root,src);AssetDatabase.CreateAsset(module,dst);
                var parent=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"Parent");var child=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"Child");
                using(var so=new SerializedObject(parent)){var children=so.FindProperty("conditionalEffects");children.arraySize=1;
                    children.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue=child;so.ApplyModifiedPropertiesWithoutUndo();}
                void SetDuration(GameplayEffectAsset fx,float n){using var so=new SerializedObject(fx);so.FindProperty("duration").floatValue=n;so.ApplyModifiedPropertiesWithoutUndo();}
                GameplayEffectAsset Paste(Func<string,ActionEffectOwnership.CopyDecision> choice=null){
                    using var so=new SerializedObject(module);var array=so.FindProperty("actions");
                    SkillActionClipboard.Copy(new ApplyEffectAction{effect=parent});
                    SkillActionClipboard.Paste(array,array.arraySize,_=>true,choice);so.ApplyModifiedProperties();
                    return ((ApplyEffectAction)module.actions[module.actions.Length-1]).effect;
                }
                SetDuration(parent,2);SetDuration(child,3);var old=Paste();
                var oldChild=(GameplayEffectAsset)AssetReferences.Children(old).Single();SetDuration(old,7);SetDuration(oldChild,9);
                int calls=0;
                Assert.Throws<OperationCanceledException>(()=>Paste(_=>ActionEffectOwnership.CopyDecision.Cancel));Assert.AreEqual(1,module.actions.Length);
                var fresh=Paste(msg=>{calls++;StringAssert.Contains(AssetReferences.Key(parent),msg);StringAssert.Contains(AssetReferences.Key(child),msg);
                    return ActionEffectOwnership.CopyDecision.New;});
                Assert.AreEqual(1,calls);Assert.AreNotSame(old,fresh);
                var newChild=(GameplayEffectAsset)AssetReferences.Children(fresh).Single();
                Assert.AreNotSame(oldChild,newChild);Assert.AreEqual(7,old.Definition.Duration);Assert.AreEqual(9,oldChild.Definition.Duration);
                Assert.AreEqual(2,fresh.Definition.Duration);Assert.AreEqual(3,newChild.Definition.Duration);
                Assert.AreSame(fresh,Paste(_=>throw new Exception("unchanged copy must be reused")));
                SetDuration(newChild,11); // Only the destination changed. Accept the actual nested tree.
                Assert.AreSame(fresh,Paste(_=>ActionEffectOwnership.CopyDecision.Reuse));
                Assert.AreSame(fresh,Paste(_=>throw new Exception("nested reuse must be remembered")));
                using(var so=new SerializedObject(module)){
                    var list=so.FindProperty("actions");SkillActionClipboard.Copy(new ApplyEffectAction{effect=child});
                    SkillActionClipboard.Paste(list,list.arraySize,null,_=>throw new Exception("child acknowledgement lost"));
                    so.ApplyModifiedProperties();Assert.AreSame(newChild,((ApplyEffectAction)module.actions[module.actions.Length-1]).effect);
                }
                SetDuration(newChild,12);bool askedAgain=false;
                Assert.AreSame(fresh,Paste(_=>{askedAgain=true;return ActionEffectOwnership.CopyDecision.Reuse;}));Assert.IsTrue(askedAgain);
                SetDuration(parent,4); // The child is unchanged and exact, but New isolates the entire private tree.
                var third=Paste(_=>ActionEffectOwnership.CopyDecision.New);
                var thirdChild=(GameplayEffectAsset)AssetReferences.Children(third).Single();
                Assert.AreNotSame(newChild,thirdChild);Assert.AreEqual(3,thirdChild.Definition.Duration);
                Assert.AreEqual(12,newChild.Definition.Duration);
                var another=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"New effect");SetDuration(third,12);
                var batch=new ConditionalAction{whenTrue=new SkillAction[]{new ApplyEffectAction{effect=parent},new ApplyEffectAction{effect=another}}};
                using(var so=new SerializedObject(module)){
                    var list=so.FindProperty("actions");int count=list.arraySize;SkillActionClipboard.Copy(batch);bool asked=false;
                    Assert.Throws<OperationCanceledException>(()=>SkillActionClipboard.Paste(list,count,msg=>{
                        asked=true;StringAssert.Contains(AssetReferences.Key(another),msg);return false;
                    },_=>ActionEffectOwnership.CopyDecision.Reuse));
                    Assert.IsTrue(asked);Assert.AreEqual(count,list.arraySize);
                    Assert.AreSame(third,Paste(_=>ActionEffectOwnership.CopyDecision.Reuse));
                    Assert.AreSame(third,Paste(_=>throw new Exception("accepted nested paste")));
                }
            }finally{AssetDatabase.DeleteAsset(src);AssetDatabase.DeleteAsset(dst);}
        }
        [Test] public void DuplicatingAnActionPreservesThePreviousClipboard(){
            var m=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                SkillActionClipboard.Copy(new ConditionalAction());
                using var so=new SerializedObject(m);var list=so.FindProperty("actions");
                SkillActionClipboard.Duplicate(list,0,new SendGameplayEventAction());
                SkillActionClipboard.Paste(list,1);so.ApplyModifiedProperties();
                Assert.IsInstanceOf<SendGameplayEventAction>(m.actions[0]);Assert.IsInstanceOf<ConditionalAction>(m.actions[1]);
                Assert.AreNotEqual(m.actions[0].Id,m.actions[1].Id);
            }finally{UnityEngine.Object.DestroyImmediate(m);}
        }
        [Test] public void FailedOuterTransactionRollsBackPublishedModuleFiles()
        {
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASRollbackCaller.asset");
            var mp=AssetDatabase.GenerateUniqueAssetPath("Assets/GASRollbackModule.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.activationActions=new SkillAction[]{new ConditionalAction()};
            AssetDatabase.CreateAsset(root,path);
            try{
                Assert.Throws<ArgumentException>(()=>EditCommand.Transaction(null,"批量",root,()=>{
                    SkillFlowCommands.Extract(null,root,"activationActions",0,1,mp);throw new ArgumentException("rollback");
                }));
                Assert.IsFalse(System.IO.File.Exists(mp));Assert.IsInstanceOf<ConditionalAction>(root.activationActions[0]);
                Assert.IsFalse(ModuleFileJournal.instance.entries.Any(e=>e.path==mp));
            }finally{AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(mp);}
        }
        [Test] public void LockedRollbackFileDoesNotReplaceTheAuthoringError(){
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASFailedRollback.asset");
            var module=AssetDatabase.GenerateUniqueAssetPath("Assets/GASLockedModule.asset");
            var sibling=AssetDatabase.GenerateUniqueAssetPath("Assets/GASRollbackSibling.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.activationActions=new SkillAction[]{new ConditionalAction(),new ConditionalAction()};
            AssetDatabase.CreateAsset(root,path);System.IO.FileStream locked=null;
            try{
                var original=new ArgumentException("original authoring error");
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("IOException:.*"+System.IO.Path.GetFileName(module)));
                var result=Assert.Throws<ArgumentException>(()=>EditCommand.Transaction(null,"回滚失败",root,()=>{
                    SkillFlowCommands.Extract(null,root,"activationActions",0,1,sibling);
                    SkillFlowCommands.Extract(null,root,"activationActions",1,1,module);
                    locked=System.IO.File.Open(module,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.None);
                    throw original;
                }));
                Assert.AreSame(original,result);Assert.IsTrue(root.activationActions.All(a=>a is ConditionalAction));
                Assert.IsFalse(System.IO.File.Exists(sibling));Assert.IsTrue(System.IO.File.Exists(module));
                Assert.IsFalse(ModuleFileJournal.instance.entries.Any(e=>e.path==module || e.path==sibling));
                locked.Dispose();locked=null;
                EditCommand.Run(null,"无关编辑",root,so=>so.FindProperty("m_Name").stringValue="renamed");
                Undo.PerformUndo();Undo.PerformRedo();ModuleFileUndo.Prune();Assert.IsTrue(System.IO.File.Exists(module),"unrelated history must not retry deletion");
            }finally{locked?.Dispose();AssetDatabase.DeleteAsset(sibling);AssetDatabase.DeleteAsset(module);AssetDatabase.DeleteAsset(path);}
        }
        [Test] public void UndoAndRedoContinuePastALockedModule(){
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASReplay"+n+".asset");
            var path=P("Owner");var good=P("Good");var bad=P("Locked");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.activationActions=new SkillAction[]{new ConditionalAction(),new ConditionalAction()};
            AssetDatabase.CreateAsset(root,path);
            try{
                EditCommand.Transaction(null,"提取两个模块",root,()=>{
                    SkillFlowCommands.Extract(null,root,"activationActions",0,1,good);
                    SkillFlowCommands.Extract(null,root,"activationActions",1,1,bad);
                });
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("IOException:.*"+System.IO.Path.GetFileName(bad)));
                using(var held=System.IO.File.Open(bad,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.None))Undo.PerformUndo();
                Assert.IsFalse(System.IO.File.Exists(good));Assert.IsTrue(System.IO.File.Exists(bad));
                Assert.IsFalse(ModuleFileJournal.instance.entries.Any(e=>e.path==bad));
                Undo.PerformRedo();Assert.AreEqual(good,AssetDatabase.GetAssetPath(((CallModuleAction)root.activationActions[0]).module));
                Assert.IsTrue(System.IO.File.Exists(bad));
            }finally{AssetDatabase.DeleteAsset(bad);AssetDatabase.DeleteAsset(good);AssetDatabase.DeleteAsset(path);}
        }
        [Test] public void FailedRedoKeepsARecoverableCopyAndContinuesWithOtherModules(){
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASRedo"+n+".asset");
            var path=P("Owner");var bad=P("Failed");var good=P("Good");string backup=null;
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.activationActions=new SkillAction[]{new ConditionalAction(),new ConditionalAction()};
            AssetDatabase.CreateAsset(root,path);
            try{
                var fx=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"private damage");
                var childFx=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"private child");
                using(var edit=new SerializedObject(fx)){edit.FindProperty("duration").floatValue=7.25f;
                    var children=edit.FindProperty("conditionalEffects");children.arraySize=1;
                    children.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue=childFx;edit.ApplyModifiedPropertiesWithoutUndo();}
                root.activationActions[0]=new ApplyEffectAction{effect=fx};var actionId=root.activationActions[0].Id;
                void CheckInline(){
                    Assert.IsInstanceOf<ApplyEffectAction>(root.activationActions[0]);var restored=(ApplyEffectAction)root.activationActions[0];
                    Assert.AreEqual(actionId,restored.Id);Assert.IsTrue(restored.effect!=null,"Private effect lost during replay.");
                    Assert.AreEqual(path,AssetDatabase.GetAssetPath(restored.effect));Assert.AreEqual(7.25f,restored.effect.Definition.Duration);
                    var child=AssetReferences.Children(restored.effect).OfType<GameplayEffectAsset>().Single();Assert.AreEqual("private child",child.name);
                    Assert.AreEqual(path,AssetDatabase.GetAssetPath(child));
                    Assert.AreEqual(2,AssetDatabase.LoadAllAssetsAtPath(path).OfType<GameplayEffectAsset>().Count(),"Only the parent and child currently in use belong in the skill file.");
                }
                EditCommand.Transaction(null,"提取两个模块",root,()=>{
                    SkillFlowCommands.Extract(null,root,"activationActions",0,1,bad,_=>true);
                    SkillFlowCommands.Extract(null,root,"activationActions",1,1,good);
                });
                Undo.PerformUndo();var e=ModuleFileJournal.instance.entries.Single(x=>x.path==bad);
                var data=e.data;var meta=e.meta;Assert.NotNull(data);
                e.path="Assets/"+Guid.NewGuid().ToString("N")+"/"+System.IO.Path.GetFileName(bad);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("DirectoryNotFoundException:.*"+System.IO.Path.GetFileName(bad)));
                string saved=System.IO.Path.GetFullPath(ModuleFileUndo.RecoveryFolder);
                Undo.PerformRedo();CheckInline();
                Assert.AreEqual(good,AssetDatabase.GetAssetPath(((CallModuleAction)root.activationActions[1]).module));

                backup=System.IO.Directory.GetFiles(saved,System.IO.Path.GetFileName(bad),System.IO.SearchOption.AllDirectories)
                    .Single(file=>System.IO.File.ReadAllBytes(file+".meta").SequenceEqual(meta));
                CollectionAssert.AreEqual(data,System.IO.File.ReadAllBytes(backup));
                var repair=((ApplyEffectAction)root.activationActions[0]).effect;
                var repairChild=AssetReferences.Children(repair).OfType<GameplayEffectAsset>().Single();
                EditCommand.Transaction(null,"修改修复效果",repair,()=>{
                    using var edit=new SerializedObject(repair);edit.FindProperty("duration").floatValue=11;edit.ApplyModifiedProperties();
                });
                Undo.PerformUndo();CheckInline();
                Undo.PerformUndo();CheckInline();
                Assert.IsTrue(repair!=null && repairChild!=null,"The next redo of the edit still targets these objects.");
                Assert.IsFalse(EditorUtility.IsPersistent(repair));Assert.IsFalse(EditorUtility.IsPersistent(repairChild));
                Undo.PerformRedo();CheckInline();Assert.AreSame(repair,((ApplyEffectAction)root.activationActions[0]).effect);
                Undo.PerformRedo();Assert.AreEqual(11,repair.Definition.Duration);
                Assert.AreSame(repairChild,AssetReferences.Children(repair).OfType<GameplayEffectAsset>().Single(),"Undo state of the edited parent still resolves its child.");
                AssetDatabase.SaveAssetIfDirty(root);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
                Assert.AreEqual(11,((ApplyEffectAction)root.activationActions[0]).effect.Definition.Duration);
                Undo.PerformUndo();CheckInline();
                for(int i=0;i<2;i++){Undo.PerformUndo();CheckInline();Undo.PerformRedo();CheckInline();
                    Assert.IsFalse(System.IO.File.Exists(e.path));Assert.IsTrue(System.IO.File.Exists(good));}
                AssetDatabase.SaveAssetIfDirty(root);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);CheckInline();
            }finally{
                if(backup!=null)ModuleFileUndo.ClearRecoveryDirectory(System.IO.Path.GetDirectoryName(backup));
                AssetDatabase.DeleteAsset(good);AssetDatabase.DeleteAsset(bad);AssetDatabase.DeleteAsset(path);
            }
        }
        [Test] public void AReferencedRepairIsNotSharedWithTheNextPrivateTree(){
            string P(string s)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASRepair"+s+".asset");
            string ownerPath=P("Source"),refPath=P("Consumer"),modulePath=P("Module"),backup=null;
            var owner=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            AssetDatabase.CreateAsset(owner,ownerPath);
            try{
                var original=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(owner,"parent");
                var child=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(owner,"child");
                using(var edit=new SerializedObject(original)){var children=edit.FindProperty("conditionalEffects");children.arraySize=1;
                    children.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue=child;edit.ApplyModifiedPropertiesWithoutUndo();}
                owner.activationActions=new SkillAction[]{new ApplyEffectAction{effect=original}};
                SkillFlowCommands.Extract(null,owner,"activationActions",0,1,modulePath,_=>true);
                Undo.PerformUndo();var e=ModuleFileJournal.instance.entries.Single(x=>x.path==modulePath);
                e.path="Assets/"+Guid.NewGuid().ToString("N")+"/"+System.IO.Path.GetFileName(modulePath);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("(?s)动作已恢复为内联.*DirectoryNotFoundException:"));
                Undo.PerformRedo();backup=e.recovery;
                var parent=((ApplyEffectAction)owner.activationActions[0]).effect;
                var retained=AssetReferences.Children(parent).OfType<GameplayEffectAsset>().Single();
                var consumer=ScriptableObject.CreateInstance<EffectReferenceFixture>();
                consumer.effect=retained;AssetDatabase.CreateAsset(consumer,refPath);AssetDatabase.SaveAssetIfDirty(consumer);
                Undo.PerformUndo();
                Assert.IsFalse(EditorUtility.IsPersistent(parent));Assert.IsTrue(EditorUtility.IsPersistent(retained));
                Undo.PerformRedo();
                Assert.AreSame(parent,((ApplyEffectAction)owner.activationActions[0]).effect,"unshared parent's Undo identity must survive");
                var privateChild=AssetReferences.Children(parent).OfType<GameplayEffectAsset>().Single();
                Assert.AreNotSame(retained,privateChild);Assert.AreSame(retained,consumer.effect);
                AssetDatabase.SaveAssetIfDirty(owner);AssetDatabase.ImportAsset(ownerPath,ImportAssetOptions.ForceSynchronousImport);
                Assert.AreEqual(3,AssetDatabase.LoadAllAssetsAtPath(ownerPath).OfType<GameplayEffectAsset>().Count());
                Assert.AreNotSame(consumer.effect,AssetReferences.Children(((ApplyEffectAction)owner.activationActions[0]).effect).Single());
                Undo.PerformUndo();Undo.ClearAll();ModuleFileUndo.Prune();
                Assert.IsTrue(parent==null && privateChild==null,"discarded history releases parked objects only");Assert.IsTrue(consumer.effect!=null);
            }finally{
                if(backup!=null)ModuleFileUndo.ClearRecoveryDirectory(System.IO.Path.GetDirectoryName(backup));
                AssetDatabase.DeleteAsset(refPath);AssetDatabase.DeleteAsset(modulePath);AssetDatabase.DeleteAsset(ownerPath);
            }
        }
        [Test] public void UnrelatedUndoDoesNotRepairASlotOrSaveDirtySkillData(){
            string path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASUndoIsolation.asset");
            string mp=AssetDatabase.GenerateUniqueAssetPath("Assets/GASUndoIsolationModule.asset");string backup=null;
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();AssetDatabase.CreateAsset(root,path);
            var unrelated=ScriptableObject.CreateInstance<EffectReferenceFixture>();
            try{
                var sibling=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"unrelated effect");
                var fx=SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root,"action effect");
                root.activationActions=new SkillAction[]{new ApplyEffectAction{effect=fx}};
                SkillFlowCommands.Extract(null,root,"activationActions",0,1,mp,_=>true);
                Undo.PerformUndo();var e=ModuleFileJournal.instance.entries.Single(x=>x.path==mp);
                e.path="Assets/"+Guid.NewGuid().ToString("N")+"/"+System.IO.Path.GetFileName(mp);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("(?s)动作已恢复为内联.*DirectoryNotFoundException:"));
                Undo.PerformRedo();backup=e.recovery;AssetDatabase.SaveAssetIfDirty(root);
                // A non-undoable tool installs a broken call with the EXACT extraction ID.
                // A stray replay will eagerly repair it, which makes this a behavioral tripwire.
                var call=JsonUtility.FromJson<CallModuleAction>(e.callJson);call.module=null;root.activationActions[0]=call;
                EditorUtility.SetDirty(root);
                const string marker="unsaved-sibling-edit-772";sibling.name=marker;EditorUtility.SetDirty(sibling);
                var written=System.IO.File.ReadAllBytes(path);
                Undo.IncrementCurrentGroup();Undo.RegisterCompleteObjectUndo(unrelated,"unrelated edit");unrelated.skill=root;Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();Assert.IsInstanceOf<CallModuleAction>(root.activationActions[0]);Assert.AreEqual(call.Id,root.activationActions[0].Id);CollectionAssert.AreEqual(written,System.IO.File.ReadAllBytes(path));
                Undo.PerformUndo();Assert.IsInstanceOf<ApplyEffectAction>(root.activationActions[0]);
                Assert.AreEqual(marker,sibling.name);StringAssert.DoesNotContain(marker,System.IO.File.ReadAllText(path));
                Undo.PerformRedo();Assert.IsInstanceOf<ApplyEffectAction>(root.activationActions[0]);
                StringAssert.DoesNotContain(marker,System.IO.File.ReadAllText(path));Assert.IsTrue(EditorUtility.IsDirty(sibling));
                AssetDatabase.SaveAssetIfDirty(root);StringAssert.Contains(marker,System.IO.File.ReadAllText(path));
            }finally{
                UnityEngine.Object.DestroyImmediate(unrelated);
                if(backup!=null)ModuleFileUndo.ClearRecoveryDirectory(System.IO.Path.GetDirectoryName(backup));
                AssetDatabase.DeleteAsset(mp);AssetDatabase.DeleteAsset(path);
            }
        }
        [TestCase(true,false)] [TestCase(false,false)] [TestCase(false,true)]
        public void FailedPublicationReportsWhatWasLeftInTheOwner(bool alreadyInline,bool invalidSnapshot){
            string P(string suffix)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASReport"+suffix+".asset");
            var path=P("Original");var currentPath=P("Current");var mp=P("Module");string backup=null;
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();var current=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            try{
                AssetDatabase.CreateAsset(root,path);AssetDatabase.CreateAsset(current,currentPath);
                var action=new ConditionalAction();root.activationActions=new SkillAction[]{action};
                // Equivalent to a freshly loaded owner that Unity's object undo did not target:
                // either the slice is already inline or the user replaced it with another action.
                var stable=action.Id;
                current.activationActions=new SkillAction[]{alreadyInline?JsonUtility.FromJson<ConditionalAction>(JsonUtility.ToJson(action)):new ConditionalAction()};
                SkillFlowCommands.Extract(null,root,"activationActions",0,1,mp);
                Undo.PerformUndo();var e=ModuleFileJournal.instance.entries.Single(x=>x.path==mp);
                if(invalidSnapshot)e.original="{";else e.owner=GlobalObjectId.GetGlobalObjectIdSlow(current).ToString();
                e.path="Assets/"+Guid.NewGuid().ToString("N")+"/"+System.IO.Path.GetFileName(mp);
                var message=invalidSnapshot?"动作未恢复：内联修复抛出异常":alreadyInline?"无需恢复：原动作已经内联":"动作未恢复：原动作槽位已更改";
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex(message));
                Undo.PerformRedo();backup=e.recovery;Assert.IsNotNull(backup);
                Assert.AreEqual(alreadyInline,current.activationActions[0].Id==stable);
                Assert.IsInstanceOf<ConditionalAction>(current.activationActions[0]);
            }finally{
                if(backup!=null)ModuleFileUndo.ClearRecoveryDirectory(System.IO.Path.GetDirectoryName(backup));
                AssetDatabase.DeleteAsset(mp);AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(currentPath);
            }
        }
        [Test] public void FailedParentRedoDoesNotPublishOrphanFiles(){
            string P(string name)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASParentFailure"+name+".asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.activationActions=new SkillAction[]{new ConditionalAction()};
            var path=P("Owner");var parent=P("Parent");var child=P("Child");AssetDatabase.CreateAsset(root,path);
            var backups=new List<string>();
            try{
                SkillFlowCommands.Extract(null,root,"activationActions",0,1,parent);
                var module=((CallModuleAction)root.activationActions[0]).module;
                SkillFlowCommands.Extract(null,module,"actions",0,1,child);
                Undo.PerformUndo();Undo.PerformUndo();
                var outer=ModuleFileJournal.instance.entries.Single(e=>e.path==parent);
                var inner=ModuleFileJournal.instance.entries.Single(e=>e.path==child);
                // Inject a well-formed Unity asset of the wrong type: WritePair and import succeed,
                // but the replay cannot bind it as a module. It must remove its own file pair.
                AssetDatabase.SaveAssetIfDirty(root);outer.data=System.IO.File.ReadAllBytes(path);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("IOException:.*恢复的文件无法加载为模块"));
                Undo.PerformRedo();backups.Add(outer.recovery);
                Assert.IsInstanceOf<ConditionalAction>(root.activationActions[0]);Assert.IsFalse(System.IO.File.Exists(parent));
                Assert.IsFalse(System.IO.File.Exists(parent+".meta"));
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,new System.Text.RegularExpressions.Regex("(?s)动作未恢复：所属资产不存在.*IOException:.*所属资产没有恢复"));
                Undo.PerformRedo();backups.Add(inner.recovery);
                Assert.IsFalse(System.IO.File.Exists(child));Assert.IsFalse(System.IO.File.Exists(child+".meta"));
                Assert.IsTrue(backups.All(System.IO.File.Exists));
                Undo.PerformUndo();Undo.PerformUndo();Undo.PerformRedo();Undo.PerformRedo();
                Assert.IsInstanceOf<ConditionalAction>(root.activationActions[0]);Assert.IsFalse(System.IO.File.Exists(child));
            }finally{
                foreach(var backup in backups.Where(p=>p!=null))ModuleFileUndo.ClearRecoveryDirectory(System.IO.Path.GetDirectoryName(backup));
                AssetDatabase.DeleteAsset(child);AssetDatabase.DeleteAsset(parent);AssetDatabase.DeleteAsset(path);
            }
        }
        [Test] public void NestedExtractionJournalSurvivesParentGroupNameAndPruning()
        {
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASBatchRoot.asset");var mp=AssetDatabase.GenerateUniqueAssetPath("Assets/GASBatchModule.asset");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.activationActions=new SkillAction[]{new ConditionalAction()};
            AssetDatabase.CreateAsset(root,path);
            string copyPath=AssetDatabase.GenerateUniqueAssetPath("Assets/GASBatchDuplicate.asset");
            try{
                using var doc=new SkillDocument();doc.Open(root);
                EditCommand.Transaction(doc,"批量调整",root,()=>SkillFlowCommands.Extract(doc,root,"activationActions",0,1,mp));
                ModuleFileUndo.Prune();Undo.PerformUndo();Assert.IsFalse(System.IO.File.Exists(mp));Assert.IsInstanceOf<ConditionalAction>(root.activationActions[0]);
                ModuleFileUndo.Prune();Undo.PerformRedo();Assert.AreEqual(mp,AssetDatabase.GetAssetPath(((CallModuleAction)root.activationActions[0]).module));
                SkillAssetFactory.Duplicate(root,copyPath);Undo.PerformUndo();Assert.IsTrue(System.IO.File.Exists(mp));
                AssetDatabase.DeleteAsset(mp);Undo.PerformRedo();
                Assert.AreEqual(mp,AssetDatabase.GetAssetPath(((CallModuleAction)root.activationActions[0]).module));
            }finally{AssetDatabase.DeleteAsset(copyPath);AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(mp);}
        }
        [Test] public void CallerCycleStillChecksTheDetachedPathIntoAnEditedModule()
        {
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASCyclicCaller"+n+".asset");
            var ap=P("A");var bp=P("B");var mp=P("Leaf");
            var a=ScriptableObject.CreateInstance<SkillActionModuleAsset>();var b=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            var leaf=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                AssetDatabase.CreateAsset(a,ap);AssetDatabase.CreateAsset(b,bp);AssetDatabase.CreateAsset(leaf,mp);
                a.actions=new SkillAction[]{new CallModuleAction{module=b},new WaitSkillAction{onCancel=new SkillAction[]{new CallModuleAction{module=leaf}}}};
                b.actions=new SkillAction[]{new CallModuleAction{module=a}};
                leaf.actions=new SkillAction[]{new WaitSkillAction()};using var so=new SerializedObject(leaf);
                var error=Assert.Throws<SkillActionException>(()=>SkillFlowCommands.ValidateOwner(so,null));
                StringAssert.DoesNotContain("超过八层",error.Message);
                // The caller cycle consumes no real nesting budget in the edited leaf.
                SkillAction deep=new ApplyEffectAction();
                for(int n=0;n<5;n++)deep=new ConditionalAction{whenTrue=new[]{deep}};
                leaf.actions=new[]{deep};
                Assert.DoesNotThrow(()=>SkillFlowCommands.ValidateOwner(so,null));
            }finally{foreach(var p in new[]{ap,bp,mp})AssetDatabase.DeleteAsset(p);}
        }
        [Test] public void FailedExtractionPreservesActionsAndDoesNotPublishAFile()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            var path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASFailedExtraction.asset");
            try{
                var call=new SpawnPrefabAction{count=new SkillNumber(1){parameterId="deleted"}};
                root.activationActions=new SkillAction[]{call};
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(null,root,"activationActions",0,1,path));
                Assert.AreEqual(call.Id,root.activationActions[0].Id);Assert.IsFalse(System.IO.File.Exists(path));
                Assert.IsFalse(System.IO.File.Exists(path+".meta"));
                // Failure after the list has actually been replaced must roll the transaction back too.
                SkillAction nested=new ApplyEffectAction();
                for(int i=0;i<7;i++)nested=new ConditionalAction{whenTrue=new[]{nested}};
                root.activationActions=new[]{nested};
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Extract(null,root,"activationActions",0,1,path));
                Assert.AreEqual(nested.Id,root.activationActions[0].Id);Assert.IsInstanceOf<ConditionalAction>(root.activationActions[0]);
                Assert.IsFalse(System.IO.File.Exists(path));Assert.IsFalse(System.IO.File.Exists(path+".meta"));
            }finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void ModuleEditChecksOnlyItsInheritedCallsiteConstraints()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();root.name="Caller";
            var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            string path=AssetDatabase.GenerateUniqueAssetPath("Assets/GASCaller.asset"),mp=AssetDatabase.GenerateUniqueAssetPath("Assets/GASCallee.asset");
            try{
                AssetDatabase.CreateAsset(module,mp);
                var call=new CallModuleAction{module=module};
                var bad=new WaitSkillAction{onCancel=new SkillAction[]{new WaitSkillAction()}};
                root.activationActions=new SkillAction[]{bad,call};AssetDatabase.CreateAsset(root,path);
                using var doc=new SkillDocument();doc.Open(root);
                module.actions=new SkillAction[]{new WaitSkillAction()};
                using var data=new SerializedObject(module);
                Assert.DoesNotThrow(()=>SkillFlowCommands.ValidateOwner(data,doc),"Bad sibling is outside the module call path.");
                root.activationActions=new SkillAction[]{new WaitSkillAction{onCancel=new SkillAction[]{call}}}; // No Notify, no SetDirty.
                var error=Assert.Throws<SkillActionException>(()=>SkillFlowCommands.ValidateOwner(data,doc));
                Assert.AreEqual(call.Id,error.ActionId);StringAssert.Contains(path,error.Message);
            }finally{AssetDatabase.DeleteAsset(path);AssetDatabase.DeleteAsset(mp);}
        }
        [Test] public void UnopenedCallerRetargetWithoutUndoIsSeenBeforeModuleEdit()
        {
            string P(string n)=>AssetDatabase.GenerateUniqueAssetPath("Assets/GASRetarget"+n+".asset");
            string rootPath=P("Root"),ap=P("A"),bp=P("B");
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            var a=ScriptableObject.CreateInstance<SkillActionModuleAsset>();var b=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                AssetDatabase.CreateAsset(a,ap);AssetDatabase.CreateAsset(b,bp);
                var call=new CallModuleAction{module=a};
                root.activationActions=new SkillAction[]{new WaitSkillAction{onCancel=new SkillAction[]{call}}};
                AssetDatabase.CreateAsset(root,rootPath);
                b.actions=new SkillAction[]{new WaitSkillAction()};using var module=new SerializedObject(b);
                SkillFlowCommands.ValidateOwner(module,null);
                using var caller=new SerializedObject(root);
                caller.FindProperty("activationActions.Array.data[0].onCancel.Array.data[0].module").objectReferenceValue=b;
                caller.ApplyModifiedPropertiesWithoutUndo();
                var error=Assert.Throws<SkillActionException>(()=>SkillFlowCommands.ValidateOwner(module,null));
                Assert.AreEqual(call.Id,error.ActionId);StringAssert.Contains(rootPath,error.Message);
            }finally{foreach(string p in new[]{rootPath,ap,bp})AssetDatabase.DeleteAsset(p);}
        }
        [Test] public void DetachedModuleAndLegacySpawnerAreRejectedAndAliasedActionsCannotBuild()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                var spawner=new RunSpawnerAction();
                var wait=new WaitSkillAction{onCancel=new SkillAction[]{spawner}};
                root.activationActions=new SkillAction[]{wait};using var data=new SerializedObject(root);
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.ValidateOwner(data,null));
                module.actions=new SkillAction[]{new WaitSkillAction()};
                wait.onCancel=new SkillAction[]{new CallModuleAction{module=module}};data.Update();
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.ValidateOwner(data,null));
                var shared=new WaitSkillAction();root.activationActions=new SkillAction[]{new ConditionalAction{whenTrue=new SkillAction[]{shared},whenFalse=new SkillAction[]{shared}}};
                Assert.Throws<SkillActionException>(()=>root.BuildActions());
            }finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(module);}
        }
        [Test] public void DetachedBranchRejectsWaitAndRollsBackMove()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            try{
                var wait=new WaitSkillAction();root.activationActions=new SkillAction[]{new LaunchProjectileAction(),wait};
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Move(null,root,"activationActions.Array.data[1]","activationActions.Array.data[0].onHit",0));
                Assert.AreEqual(2,root.activationActions.Length);Assert.AreEqual(0,((LaunchProjectileAction)root.activationActions[0]).onHit.Length);
            }finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void PreviewUsesSameSectorConeAndLineBounds()
        {
            var sector=new TargetQuery{kind=TargetQueryKind.Sector,radius=new SkillNumber(3),angle=new SkillNumber(90)}.Build();var values=new EffectEvaluationContext(1);
            Assert.IsTrue(SkillPreviewSimulation.Contains(sector,values,Vector3.forward*2));
            Assert.IsFalse(SkillPreviewSimulation.Contains(sector,values,Vector3.right*2));Assert.IsFalse(SkillPreviewSimulation.Contains(sector,values,Vector3.forward*4));
            var line=new TargetQuery{kind=TargetQueryKind.Line,radius=new SkillNumber(3),width=new SkillNumber(1)}.Build();
            Assert.IsFalse(SkillPreviewSimulation.Contains(line,values,new Vector3(.51f,0,2)));Assert.IsTrue(SkillPreviewSimulation.Contains(line,values,new Vector3(.49f,0,2)));
        }
        [Test] public void RingRetainsLatest4096InOrder()
        {
            var history=new SkillHistory();
            for(int i=0;i<4107;i++) history.Add(new SkillTrace(TraceKind.动作开始,"记录",cast:i));
            Assert.AreEqual(4096,history.Count);Assert.AreEqual(11,history[0].Cast); Assert.AreEqual(4106,history[4095].Cast);
        }
        private sealed class PresentationProbe:ISkillCameraService,ISkillTimeService,ISkillRigService,ISkillPresentationLease
        {
            public int Opened,Closed;public float Last;
            private ISkillPresentationLease Open(){Opened++;return this;}
            public ISkillPresentationLease Shake(SkillExecutionContext c,Vector3 dir,float hz)=>Open();
            public ISkillPresentationLease GlobalScale(SkillExecutionContext c)=>Open();
            public ISkillPresentationLease HitStop(SkillExecutionContext c,float seconds)=>Open();
            public float LocalScale(AbilitySystemComponent u)=>1;
            public ISkillPresentationLease Aim(SkillExecutionContext c,string chain)=>Open();
            public void Sample(float value)=>Last=value;
            public void Dispose()=>Closed++;
        }
        private sealed class CaptureDriver : IAnimationDriver
        {
            public string Last;public float Play(string name,float duration,bool loop){Last=name;return 1;}
            public void Advance(float t){} public void Stop(){}
        }
        [Serializable] private sealed class CaptureBackend : AnimationBackend
        {
            public override string BackendId=>"test";
            public override bool Validate(GameObject go,out string error){error=null;return true;}
            public override IAnimationDriver CreateDriver(GameObject go)=>new CaptureDriver();
        }
        [Test] public void EquipmentOverridesSkillVariantAndInvalidEntriesFallThrough()
        {
            var go=new GameObject();var host=go.AddComponent<SkillAnimator>();host.SetBackend(new CaptureBackend());
            var driver=(CaptureDriver)host.Driver;var skill=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            SkillClipOverrideSet Set(string dest){
                var result=ScriptableObject.CreateInstance<SkillClipOverrideSet>();
                result.clips=new[]{new SkillClipOverrideSet.Entry{original="base",replacement=dest}};return result;}
            var basis=skill.clipOverrides=Set("variant");var equipment=Set("equipment");var invalid=Set(null);var clip=new AnimationClip();
            invalid.clips[0].clip=clip; // This backend accepts names only.
            try{
                var asc=new AbilitySystemComponent();var spec=asc.GrantAbility(new GameplayAbilityDefinition());
                var c=new SkillExecutionContext(skill,spec,null,new EffectEvaluationContext(1),null);
                string Play(){_=new AnimationTask(new SkillAnimationDefinition("base",1),driver,taskContext:c.Services);return driver.Last;}
                Assert.AreEqual("variant",Play());host.PushOverride(equipment);host.PushOverride(invalid);
                Assert.AreEqual("equipment",Play());host.PopOverride(equipment);Assert.AreEqual("variant",Play());
                skill.clipOverrides=null;Assert.AreEqual("base",Play());
            }finally{foreach(var o in new UnityEngine.Object[]{go,skill,basis,equipment,invalid,clip})UnityEngine.Object.DestroyImmediate(o);}
        }
        [Test] public void ArenaHitStopAndTimeScaleRespectParticipantsOverlapsAndExternalScale()
        {
            var go=new GameObject();var service=go.AddComponent<ZFramework.GAS.Samples.BasicCombat.ArenaPresentation>();
            var owners=new[]{new AbilitySystemComponent(),new AbilitySystemComponent(),new AbilitySystemComponent()};
            var tasks=new AnimationTask[3];SkillExecutionContext context=null;
            float saved=Time.timeScale;
            try{
                for(int i=0;i<owners.Length;i++){
                    var spec=owners[i].GrantAbility(new GameplayAbilityDefinition());owners[i].TryActivateAbility(spec);
                    tasks[i]=new AnimationTask(new SkillAnimationDefinition("clip",1),new CaptureDriver());owners[i].StartTask(spec,tasks[i]);
                    if(i==0)context=new SkillExecutionContext(null,spec,owners[1],new EffectEvaluationContext(1),new TimelineTaskContext{Time=service});}
                var h=new HitStopTrack{realSeconds=10}.Build().Create(context);h.Enter();h.Sample(0,0);
                try{
                    foreach(var asc in owners)asc.Tick(.2f);
                    Assert.AreEqual(0,tasks[0].NormalizedTime);Assert.AreEqual(0,tasks[1].NormalizedTime);Assert.AreEqual(.2f,tasks[2].NormalizedTime,.001f);
                }finally{h.Cancel();}
                owners[1].Tick(.1f);Assert.AreEqual(.1f,tasks[1].NormalizedTime,.001f);
                Time.timeScale=.8f;using var a=service.GlobalScale(context);a.Sample(.5f);using var b=service.GlobalScale(context);b.Sample(.25f);
                Assert.AreEqual(.1f,Time.timeScale,.001f);Time.timeScale=.6f;b.Dispose();
                Assert.AreEqual(.3f,Time.timeScale,.001f);a.Dispose();Assert.AreEqual(.6f,Time.timeScale,.001f);
            }finally{foreach(var asc in owners)asc.Clear();UnityEngine.Object.DestroyImmediate(go);Time.timeScale=saved;}
        }
        [Test] public void PresentationTracksReleaseEachLeaseOnceOnExitOrCancellation()
        {
            var source=new AbilitySystemComponent();
            var spec=source.GrantAbility(new GameplayAbilityDefinition());
            var probe=new PresentationProbe();var values=new EffectEvaluationContext(1);
            var context=new SkillExecutionContext(null,spec,null,values,new TimelineTaskContext{Camera=probe,Time=probe,Rig=probe});
            var noServices=new SkillExecutionContext(null,spec,null,values,null);
            foreach(var track in new PresentationTrack[]{new CameraShakeTrack(),new HitStopTrack(),new TimeScaleTrack(),new LookAtTrack()})
            {
                track.amount=AnimationCurve.Linear(0,0,1,1);var definition=track.Build();
                var h=definition.Create(context);
                h.Enter();h.Sample(.5f,.5f);Assert.AreEqual(.5f,probe.Last,.001f);h.Exit();h.Cancel();
                h.Enter();h.Cancel();h.Exit();
                var silent=definition.Create(noServices);silent.Enter();silent.Sample(.2f,.2f);silent.Cancel();
            }
            Assert.AreEqual(8,probe.Opened);Assert.AreEqual(8,probe.Closed);
        }
        [Test] public void ThreeWayMergeKeepsIndependentFieldsAndFlagsConflictingEditsAndLayout()
        {
            SkillSemanticDiff.Snapshot S(string a,string b,string layout)=>new SkillSemanticDiff.Snapshot{layout=layout,entries=new List<SkillSemanticDiff.Entry>{
                new SkillSemanticDiff.Entry{key="actions[id=A]/damage",value=a},new SkillSemanticDiff.Entry{key="actions[id=B]/radius",value=b}}};
            var conflicts=new List<string>();var basis=S("5","1","original");var ours=S("8","1","left");
            var merged=SkillSemanticDiff.Merge(basis,ours,S("5","2","original"),conflicts);
            Assert.IsEmpty(conflicts);CollectionAssert.AreEqual(new[]{"8","2"},merged.entries.Select(e=>e.value));
            SkillSemanticDiff.Merge(basis,ours,S("6","2","right"),conflicts);
            CollectionAssert.AreEquivalent(new[]{"actions[id=A]/damage","@layout"},conflicts);
        }
    }
}
