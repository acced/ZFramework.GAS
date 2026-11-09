using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class StandaloneEditorTests
    {
        [Test]
        public void EditAnAbilityAndObserveItsOwnerWithoutAPresentation()
        {
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/StandaloneSkill.asset");
            var editor = ScriptableObject.CreateInstance<SkillEditorWindow>();
            var debugger = ScriptableObject.CreateInstance<RuntimeDebugWindow>();
            var owner = new AbilitySystemComponent(1);
            try
            {
                var skill = SkillTemplateRegistry.Create(path);
                Assert.IsNull(skill.Presentation);
                Assert.IsNull(skill.Definition.Cost);
                Assert.IsNull(skill.Definition.Cooldown);
                Assert.IsEmpty(skill.activationActions);
                Assert.AreEqual(1, AssetDatabase.LoadAllAssetsAtPath(path).Length, "A new skill must not own any generated assets.");
                editor.Show(); editor.Show(skill);
                var cost = SkillAssetFactory.Create(editor.Document, typeof(GameplayEffectAsset), "Cost",
                    EffectDurationPolicy.Instant, skill, new PortId(PortKind.Cost));
                editor.Document.OpenFlow(); editor.ShowAbilitySettings(); editor.Save();
                Assert.AreEqual(2, editor.NodeCount);
                Assert.AreSame(cost, PortRules.Links(skill).Single().Child);
                Assert.IsNull(skill.Presentation);
                Assert.That(editor.Document.PlaybackDuration, Is.Zero);

                debugger.Show(); debugger.CreateGUI();
                owner.GrantAbility(skill.Definition);
                var snapshot = RuntimeSnapshot.Capture(owner, _ => "Unit");
                RuntimeDebugSession.Publish(snapshot);
                Assert.AreSame(skill, snapshot.Abilities.Single().Asset);
                Assert.IsEmpty(snapshot.Tasks);
                Assert.IsNotNull(debugger.rootVisualElement.Q<Foldout>("debug-abilities"));
                if (SkillEditorExtensions.Timeline == null)
                {
                    Assert.IsNull(editor.rootVisualElement.Q("preview-split"));
                    Assert.IsNull(debugger.rootVisualElement.Q("debug-tasks"));
                    Assert.IsFalse(PortRules.CanAttach(skill, new PortId(PortKind.Animation), cost, out _));
                }
            }
            finally
            {
                RuntimeDebugSession.Clear(); owner.Clear();
                debugger.Close(); editor.Close(); AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
