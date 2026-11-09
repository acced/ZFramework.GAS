using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class ParameterFoldoutTests
    {
        private const string Folder = "Assets/GASParameterFoldoutRegression";
        private GameplayAbilityAsset _skill;
        [SetUp] public void Setup()
        {
            TestTags.Initialize();
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "GASParameterFoldoutRegression");
            _skill = ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            _skill.parameters = new[] { new SkillParameter { displayName = "伤害系数" }, new SkillParameter { displayName = "强化伤害系数" } };
            AssetDatabase.CreateAsset(_skill, Folder + "/Skill.asset");
            SkillIndex.Invalidate();
        }
        [TearDown] public void Cleanup()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SkillEditorWindow>()) window.Close();
            AssetDatabase.DeleteAsset(Folder); Undo.ClearAll(); SkillIndex.Invalidate();
        }
        private static void Pointer(VisualElement target, EventType type, Vector2 position)
        {
            var system = new Event { type = type, mousePosition = position, button = 0, clickCount = 1 };
            if (type == EventType.MouseDown) { using var e = PointerDownEvent.GetPooled(system); e.target = target; target.SendEvent(e); }
            else { using var e = PointerUpEvent.GetPooled(system); e.target = target; target.SendEvent(e); }
        }
        private static Foldout Parameter(SkillEditorWindow window)
            => window.rootVisualElement.Q<AbilitySettingsPanel>().Query<Foldout>().ToList().FirstOrDefault(f => f.bindingPath == "parameters.Array.data[0]");
        private static void Click(Foldout fold)
        {
            var toggle = fold.Q<Toggle>(); var point = toggle.worldBound.center;
            Pointer(toggle, EventType.MouseDown, point); Pointer(toggle, EventType.MouseUp, point);
        }

        [UnityTest] public IEnumerator ParameterFoldoutClickSurvivesFocusAndDoesNotEditAsset()
        {
            var window = SkillEditorWindow.Open(); window.position = new Rect(10, 10, 1280, 900); window.Show(_skill); window.ShowAbilitySettings();
            for (int i = 0; i < 5; i++) yield return null;
            window.rootVisualElement.Q<AbilitySettingsPanel>().Query<Foldout>().ToList().First(f => f.bindingPath == "parameters").value = true;
            for (int i = 0; i < 15; i++) { window.Repaint(); yield return null; }
            var fold = Parameter(window);
            Assert.IsNotNull(fold, string.Join("\n", window.rootVisualElement.Query<Foldout>().ToList().Select(f => f.text + " : " + f.bindingPath)));
            Assert.AreEqual("伤害系数", fold.text);
            string before = EditorJsonUtility.ToJson(_skill); int changes = 0; window.Document.Changed += _ => changes++;
            for (int i = 0; i < 6; i++)
            {
                fold = Parameter(window); bool expected = !fold.value;
                var toggle = fold.Q<Toggle>(); var point = toggle.worldBound.position + new Vector2(7, toggle.worldBound.height / 2);
                Pointer(toggle, EventType.MouseDown, point);
                // Focus can arrive between mouse down and release; rebuilding here detaches the pressed control.
                if (i % 2 == 0) typeof(SkillEditorWindow).GetMethod("OnFocus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, null);
                Pointer(toggle, EventType.MouseUp, point);
                for (int frame = 0; frame < 4; frame++) { window.Repaint(); yield return null; }
                Assert.AreEqual(expected, Parameter(window).value, "Foldout click #" + i);
            }
            Assert.AreEqual(before, EditorJsonUtility.ToJson(_skill)); Assert.AreEqual(0, changes, "Expanding and focusing must not report an asset change.");
        }
        [UnityTest] public IEnumerator SerializedDataChangeIsDeferredAndFocusDetectsExternalEdit()
        {
            using var doc = new SkillDocument(); doc.Open(_skill); int changes = 0; doc.Changed += _ => changes++;
            var so = doc.Serialized(_skill); var parameters = so.FindProperty("parameters");
            parameters.isExpanded = !parameters.isExpanded;
            doc.NotifySerializedChange(_skill, "parameters");
            yield return null; Assert.AreEqual(0, changes, "Expansion is editor state only.");
            var definition = _skill.Definition;
            so.FindProperty("displayName").stringValue = "修改后的技能"; so.ApplyModifiedProperties();
            doc.NotifySerializedChange(_skill, "displayName");
            Assert.AreEqual(0, changes, "Must not rebuild controls during binding/input dispatch.");
            for (int frame = 0; frame < 10; frame++) yield return null;
            Assert.AreEqual(1, changes, "A real serialized edit must reach the document.");
            Assert.AreNotSame(definition, _skill.Definition);
            doc.RefreshIfChanged(); Assert.AreEqual(1, changes);
            using var external = new SerializedObject(_skill);
            external.FindProperty("description").stringValue = "外部编辑"; external.ApplyModifiedProperties();
            doc.RefreshIfChanged(); Assert.AreEqual(2, changes, "Focus must still detect external changes.");
        }

        [UnityTest] public IEnumerator OpeningAnotherDocumentDropsOldPendingBindingNotifications()
        {
            using var doc = new SkillDocument(); doc.Open(_skill);
            var so = doc.Serialized(_skill); so.FindProperty("displayName").stringValue = "旧文档修改"; so.ApplyModifiedProperties();
            doc.NotifySerializedChange(_skill, "displayName");
            var other = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); AssetDatabase.CreateAsset(other, Folder + "/Other.asset");
            doc.Open(other); int changes = 0; doc.Changed += _ => changes++;
            for (int frame = 0; frame < 10; frame++) yield return null;
            Assert.AreEqual(0, changes); Assert.AreSame(other, doc.Root);
        }
    }
}
