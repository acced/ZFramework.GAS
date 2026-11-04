using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(SkillParameter))]
    public sealed class SkillParameterDrawer : PropertyDrawer
    {
        private static readonly (string path, string label)[] Fields = {
            ("displayName", "显示名称"), ("description", "说明"), ("kind", "类型"),
            ("defaultValue", "默认值"), ("levels", "等级数值")
        };
        private static string Title(SerializedProperty property)
        {
            string name = property.FindPropertyRelative("displayName").stringValue;
            return string.IsNullOrWhiteSpace(name) ? "未命名参数" : name;
        }
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            // Bind expansion once; a click changes only isExpanded, never the asset or Undo.
            var fold = new Foldout { text = Title(property), value = property.isExpanded, bindingPath = property.propertyPath,
                tooltip = "参数 ID：" + property.FindPropertyRelative("id").stringValue };
            fold.AddToClassList("skill-parameter");
            foreach (var field in Fields) fold.Add(new PropertyField(property.FindPropertyRelative(field.path), field.label));
            fold.TrackPropertyValue(property.FindPropertyRelative("displayName"), _ => fold.text = Title(property));
            return fold;
        }
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
                foreach (var field in Fields) height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(property.FindPropertyRelative(field.path), true);
            return height;
        }
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(row, property.isExpanded, new GUIContent(Title(property)), true);
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (var field in Fields)
                {
                    row.y += row.height + EditorGUIUtility.standardVerticalSpacing;
                    var child = property.FindPropertyRelative(field.path); row.height = EditorGUI.GetPropertyHeight(child, true);
                    EditorGUI.PropertyField(row, child, new GUIContent(field.label), true);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
}
