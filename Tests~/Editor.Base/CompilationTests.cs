using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class CompilationTests
    {
        private const string DisabledRule = "ZFramework.GAS.Validation.Disabled.GAS_ACTION";
        private readonly List<ScriptableObject> _assets = new();

        private T Create<T>() where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            asset.name = typeof(T).Name;
            _assets.Add(asset);
            return asset;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var asset in _assets) Object.DestroyImmediate(asset);
            _assets.Clear();
        }

        [Test]
        public void StrictCheckRebuildsCachedActionsAndIgnoresDisabledRulePreferences()
        {
            var skill = Create<GameplayAbilityAsset>();
            var wait = new WaitSkillAction();
            skill.activationActions = new SkillAction[] { wait };
            skill.BuildActions();
            wait.timeout = new SkillNumber(-1);
            using var document = new SkillDocument(); document.Open(skill, false);
            using var validation = new ValidationService(document);
            bool existed = EditorPrefs.HasKey(DisabledRule), previous = EditorPrefs.GetBool(DisabledRule);
            try
            {
                EditorPrefs.SetBool(DisabledRule, true);
                validation.ValidateNow(true);
                Assert.IsFalse(validation.Issues.Any(i => i.Id == "GAS_ACTION"));
                validation.ValidateNow(strict: true);
                var issue = validation.Issues.Single(i => i.Id == "GAS_ACTION");
                Assert.AreEqual("activationActions.Array.data[0]", issue.PropertyPath);
                bool opened = false; document.FlowRequested += () => opened = true;
                SkillActionNavigation.Select(document, issue);
                Assert.AreSame(skill, document.Selection.Asset);
                Assert.AreEqual(issue.PropertyPath, document.Selection.ElementId);
                Assert.IsTrue(opened);
            }
            finally
            {
                if (existed) EditorPrefs.SetBool(DisabledRule, previous);
                else EditorPrefs.DeleteKey(DisabledRule);
            }
        }

        [Test]
        public void DefinitionConstructionErrorsAreReportedWithoutAnAbilityCaller()
        {
            var effect = Create<GameplayEffectAsset>();
            using (var serialized = new SerializedObject(effect))
            {
                serialized.FindProperty("duration").floatValue = -1;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using var document = new SkillDocument();
            var issues = new List<Issue>();
            new SkillConfigurationRule().Validate(new ValidationContext(document), effect, issues);
            Assert.IsTrue(issues.Any(i => i.Id == "GAS_DEFINITION" && i.Severity == IssueSeverity.Error));
        }

        [Test]
        public void UnregisteredRequirementTagsAreErrorsRatherThanSilentlyWeakenedGates()
        {
            var skill = Create<GameplayAbilityAsset>();
            using (var serialized = new SerializedObject(skill))
            {
                var tags = serialized.FindProperty("activationRequiredTags"); tags.arraySize = 1;
                tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "GAS.Compilation.Unknown";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using var document = new SkillDocument(); document.Open(skill, false);
            using var validation = new ValidationService(document); validation.ValidateNow(strict: true);
            Assert.IsTrue(validation.Issues.Any(i => i.Id == "GAS022" && i.Severity == IssueSeverity.Error));
        }

        [Test]
        public void InvalidHigherLevelOverrideFailsBeforeTheSkillIsGranted()
        {
            var skill = Create<GameplayAbilityAsset>();
            var parameter = new SkillParameter { kind = SkillParameterKind.Int, defaultValue = 1 };
            skill.parameters = new[] { parameter };
            skill.parameterOverrides = new[] {
                new SkillParameterOverride { parameterId = parameter.Id, kind = SkillParameterKind.Int, levels = new[] { 1f, 1.5f } }
            };
            using var document = new SkillDocument(); document.Open(skill, false);
            using var validation = new ValidationService(document); validation.ValidateNow(strict: true);
            Assert.IsTrue(validation.Issues.Any(i => i.Id == "GAS_ACTION" && i.Message.Contains("Int32")));
        }

        [Test]
        public void ActionConstraintsCheckEffectiveAuthoredOverrides()
        {
            var skill = Create<GameplayAbilityAsset>();
            var duration = new SkillParameter { defaultValue = 1 };
            skill.parameters = new[] { duration };
            skill.parameterOverrides = new[] { new SkillParameterOverride { parameterId = duration.Id, value = -1 } };
            skill.activationActions = new SkillAction[] {
                new WaitSkillAction { timeout = new SkillNumber(1) { parameterId = duration.Id } }
            };
            Assert.Throws<SkillActionException>(() => skill.BuildActions());
        }

        [Test]
        public void SharedParameterEditsInvalidateActionsBuiltBeforeTheAbilityDefinition()
        {
            var skill = Create<GameplayAbilityAsset>();
            var set = Create<SkillParameterSetAsset>();
            var duration = new SkillParameter { defaultValue = 1 };
            set.parameters = new[] { duration }; skill.parameterSet = set;
            skill.activationActions = new SkillAction[] {
                new WaitSkillAction { timeout = new SkillNumber(1) { parameterId = duration.Id } }
            };
            skill.BuildActions();
            duration.defaultValue = -1;
            DefinitionSource.InvalidateDependents(set);
            Assert.Throws<SkillActionException>(() => skill.BuildActions());
        }

        [Test]
        public void EmptyModulesStillValidateTheirParameterDeclarations()
        {
            var module = Create<SkillActionModuleAsset>();
            var parameters = Create<SkillParameterSetAsset>();
            var parameter = new SkillParameter();
            parameters.parameters = new[] { parameter, parameter }; module.parameters = parameters;
            Assert.Throws<InvalidOperationException>(() => module.Build());
        }

        [Test]
        public void DuplicateModuleBindingsAreRejectedAtTheCallSite()
        {
            var skill = Create<GameplayAbilityAsset>();
            var caller = new SkillParameter(); skill.parameters = new[] { caller };
            var local = new SkillParameter();
            var binding = new ModuleParameterBinding { moduleParameterId = local.Id, callerParameterId = caller.Id };
            var action = new InlineParameterScopeAction { parameters = new[] { local }, bindings = new[] { binding, binding } };
            skill.activationActions = new SkillAction[] { action };
            Assert.AreEqual(action.Id, Assert.Throws<SkillActionException>(() => skill.BuildActions()).ActionId);
        }

        [Test]
        public void DetachedCallbacksCannotCommitAnAbility()
        {
            var actions = new SkillAction[] { new CommitAbilityAction() };
            Assert.Throws<SkillOwnershipException>(() => new SkillActionBuildContext().BuildDetached(actions));
        }

        [TestCase(false, false, 8f)]
        [TestCase(true, false, 10f)]
        [TestCase(false, true, 1f)]
        public void DeferredCommitChargesAtTheActionAndCancellationRemainsFree(bool cancel, bool exhausted, float expected)
        {
            var skill = Create<GameplayAbilityAsset>(); skill.autoCommit = false;
            var cost = Create<GameplayEffectAsset>();
            using (var serialized = new SerializedObject(cost))
            {
                var modifiers = serialized.FindProperty("modifiers"); modifiers.arraySize = 1;
                var modifier = modifiers.GetArrayElementAtIndex(0);
                modifier.FindPropertyRelative("targetAttributeId").intValue = 7;
                modifier.FindPropertyRelative("magnitude.constant").floatValue = -2;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var serialized = new SerializedObject(skill))
            {
                serialized.FindProperty("cost").objectReferenceValue = cost;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            skill.activationActions = new SkillAction[] { new WaitSkillAction {
                timeout = new SkillNumber(.1f), onSuccess = new SkillAction[] { new CommitAbilityAction() }
            } };
            var owner = new AbilitySystemComponent(); var resource = new AttributeId(7);
            owner.Attributes.Add(resource, 10);
            var runner = new SkillRunner(owner); runner.Grant(skill);
            try
            {
                Assert.AreEqual(AbilityActivationResult.Activated, runner.TryCast(skill));
                Assert.AreEqual(10, owner.Attributes.GetCurrent(resource));
                Assert.IsFalse(runner.GetSpec(skill).IsCommitted);
                if (exhausted) owner.Attributes.Get(resource).BaseValue = 1;
                if (cancel) runner.Cancel(skill);
                owner.Tick(.2f);
                Assert.AreEqual(expected, owner.Attributes.GetCurrent(resource));
                Assert.AreEqual(!cancel && !exhausted, runner.GetSpec(skill).IsCommitted);
                Assert.IsFalse(runner.IsActive(skill));
            }
            finally { owner.Clear(); }
        }

        [Test]
        public void BatchCheckIncludesUnreferencedEffectsAndAbilitySets()
        {
            string folder = "Assets/GASCompile_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            try
            {
                var effect = Create<GameplayEffectAsset>();
                AssetDatabase.CreateAsset(effect, folder + "/Effect.asset");
                using (var serialized = new SerializedObject(effect))
                {
                    serialized.FindProperty("duration").floatValue = -1;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                var set = Create<AbilitySetAsset>(); set.abilities = new[] { new AbilitySetAsset.Entry() };
                AssetDatabase.CreateAsset(set, folder + "/Set.asset");
                var issues = BatchValidate.CheckAll();
                Assert.IsTrue(issues.Any(i => i.Asset == effect && i.Id == "GAS_DEFINITION"));
                Assert.IsTrue(issues.Any(i => i.Asset == set && i.Id == "GAS_ACTION"));
                _assets.Remove(effect); _assets.Remove(set);
            }
            finally { AssetDatabase.DeleteAsset(folder); SkillIndex.Invalidate(); }
        }
    }
}
