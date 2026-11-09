using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class FlowCommandTests
    {
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

        [Test] public void DuplicatingAnActionPreservesThePreviousClipboard(){
            var m=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            try{
                SkillActionClipboard.Copy(new ConditionalAction());
                using var so=new SerializedObject(m);var list=so.FindProperty("activationActions");
                SkillActionClipboard.Duplicate(list,0,new SendGameplayEventAction());
                SkillActionClipboard.Paste(list,1);so.ApplyModifiedProperties();
                Assert.IsInstanceOf<SendGameplayEventAction>(m.activationActions[0]);Assert.IsInstanceOf<ConditionalAction>(m.activationActions[1]);
                Assert.AreNotEqual(m.activationActions[0].Id,m.activationActions[1].Id);
            }finally{UnityEngine.Object.DestroyImmediate(m);}
        }

        [Test] public void RingRetainsLatest4096InOrder()
        {
            var history=new SkillHistory();
            for(int i=0;i<4107;i++) history.Add(new SkillTrace(TraceKind.动作开始,"记录",cast:i));
            Assert.AreEqual(4096,history.Count);Assert.AreEqual(11,history[0].Cast); Assert.AreEqual(4106,history[4095].Cast);
        }
    }
}
