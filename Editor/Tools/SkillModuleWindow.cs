using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillModuleWindow : EditorWindow
    {
        [SerializeField] private List<SkillActionModuleAsset> modules = new();
        [SerializeField] private SkillActionModuleAsset active;
        [UnityEditor.Callbacks.OnOpenAsset]
        private static bool OpenAsset(int instanceId, int line)
        {
            if (EditorUtility.EntityIdToObject(instanceId) is not SkillActionModuleAsset module) return false;
            Show(module); return true;
        }
        public static void Show(SkillActionModuleAsset module)
        {
            var window = GetWindow<SkillModuleWindow>("GAS 动作模块");
            if (!window.modules.Contains(module)) window.modules.Add(module); window.active = module; window.CreateGUI();
        }
        private void CreateGUI()
        {
            rootVisualElement.Clear(); var tabs = new Toolbar(); rootVisualElement.Add(tabs);
            modules.RemoveAll(module => module == null);
            foreach (var module in modules) tabs.Add(new ToolbarButton(() => { active = module; CreateGUI(); }) { text = module.name });
            if (active == null) return;
            rootVisualElement.Add(new HelpBox("共享模块 · 修改会影响所有引用它的技能。私有参数覆盖不会反向修改模块。", HelpBoxMessageType.Info));
            var references = SkillReferenceWindow.Find(active);
            rootVisualElement.Add(new Button(() => SkillReferenceWindow.Show(active)) { text = "查看影响范围 · " + references.Count + " 个直接引用" });
            var scroll = new ScrollView(); rootVisualElement.Add(scroll); scroll.Add(new InspectorElement(active));
        }
    }
}

