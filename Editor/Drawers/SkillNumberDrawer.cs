using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(SkillNumber))]
    public sealed class SkillNumberDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new VisualElement();
            var all = Parameters(property.serializedObject.targetObject).Where(p => p != null).ToArray();
            var names = new List<string> { "固定值" }; names.AddRange(all.Select((p, i) => p.displayName + " · " + p.kind + " #" + (i + 1)));
            var id = property.FindPropertyRelative("parameterId");
            int index = System.Array.FindIndex(all, p => p.Id == id.stringValue) + 1;
            if (!string.IsNullOrEmpty(id.stringValue) && index == 0) { names.Add("缺失参数 · " + id.stringValue); index = names.Count - 1; }
            var menu = new PopupField<string>(property.displayName, names, index); root.Add(menu);
            var constant = new PropertyField(property.FindPropertyRelative("value"), "固定值"); root.Add(constant);
            var scale = new PropertyField(property.FindPropertyRelative("coefficient"), "参数系数"); root.Add(scale);
            void Refresh() { bool bound = !string.IsNullOrEmpty(id.stringValue); constant.style.display = bound ? DisplayStyle.None : DisplayStyle.Flex; scale.style.display = bound ? DisplayStyle.Flex : DisplayStyle.None; }
            menu.RegisterValueChangedCallback(e =>
            {
                int selected = names.IndexOf(e.newValue); if (selected > all.Length) return;
                id.stringValue = selected == 0 ? "" : all[selected - 1].Id;
                property.serializedObject.ApplyModifiedProperties(); Refresh();
            });
            Refresh(); return root;
        }
        internal static IEnumerable<SkillParameter> Parameters(Object asset)
        {
            if (asset is SkillActionModuleAsset module) return module.parameters != null ? module.parameters.parameters : System.Array.Empty<SkillParameter>();
            var root = asset as GameplayAbilityAsset ?? AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(asset)) as GameplayAbilityAsset;
            if (root == null) return System.Array.Empty<SkillParameter>();
            return (root.parameterSet != null ? root.parameterSet.parameters : System.Array.Empty<SkillParameter>()).Concat(root.parameters);
        }
    }
    [CustomPropertyDrawer(typeof(SkillParameterReferenceAttribute))]
    public sealed class SkillParameterReferenceDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
            => Picker(property, SkillNumberDrawer.Parameters(property.serializedObject.targetObject), property.displayName);
        internal static VisualElement Picker(SerializedProperty property, IEnumerable<SkillParameter> parameters, string label)
        {
            var all = parameters.Where(p => p != null).ToArray();
            var names = new List<string> { "未绑定" }; names.AddRange(all.Select((p, i) => p.displayName + " · " + p.kind + " #" + (i + 1)));
            int index = System.Array.FindIndex(all, p => p.Id == property.stringValue) + 1;
            if (index == 0 && !string.IsNullOrEmpty(property.stringValue)) { names.Add("引用的参数已不存在"); index = names.Count - 1; }
            var popup = new PopupField<string>(label, names, index) { tooltip = property.stringValue };
            popup.RegisterValueChangedCallback(e =>
            {
                int selected = names.IndexOf(e.newValue); if (selected > all.Length) return;
                property.stringValue = selected == 0 ? "" : all[selected - 1].Id; property.serializedObject.ApplyModifiedProperties(); popup.tooltip = property.stringValue;
            });
            return popup;
        }
    }
    [CustomPropertyDrawer(typeof(ModuleParameterBinding))]
    public sealed class ModuleParameterBindingDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new VisualElement(); string path = property.propertyPath;
            int start = path.LastIndexOf(".bindings", System.StringComparison.Ordinal);
            var action = start >= 0 ? property.serializedObject.FindProperty(path.Substring(0, start)).managedReferenceValue as CallModuleAction : null;
            var parameters = action?.module?.parameters?.parameters ?? System.Array.Empty<SkillParameter>();
            root.Add(SkillParameterReferenceDrawer.Picker(property.FindPropertyRelative("moduleParameterId"), parameters, "模块参数"));
            root.Add(SkillParameterReferenceDrawer.Picker(property.FindPropertyRelative("callerParameterId"), SkillNumberDrawer.Parameters(property.serializedObject.targetObject), "使用技能参数"));
            return root;
        }
    }
}
