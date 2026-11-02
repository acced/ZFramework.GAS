using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(AnimationSectionReferenceAttribute))]
    public sealed class SectionReferenceDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var animation = property.serializedObject.targetObject as SkillAnimationAsset;
            var sections = animation?.sections ?? System.Array.Empty<AnimationSection>();
            var ids = sections.Select(s => s.Id).Prepend("").ToList();
            var labels = sections.Select((s, i) => (string.IsNullOrEmpty(s.displayName) ? "动画段" : s.displayName) + " · " + (i + 1)).Prepend("结束").ToList();
            int index = ids.IndexOf(property.stringValue);
            if (index < 0) { ids.Add(property.stringValue); labels.Add("已删除的动画段"); index = ids.Count - 1; }
            var popup = new PopupField<string>("下一段", labels, index);
            popup.RegisterValueChangedCallback(e => { property.serializedObject.Update(); property.stringValue = ids[labels.IndexOf(e.newValue)]; property.serializedObject.ApplyModifiedProperties(); });
            return popup;
        }
    }
}
