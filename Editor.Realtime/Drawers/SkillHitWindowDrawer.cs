using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(SkillHitWindow))]
    public sealed class SkillHitWindowDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new VisualElement();
            foreach (string name in new[] { "start", "end", "effect", "hitInterval", "targets" })
                root.Add(new PropertyField(property.FindPropertyRelative(name)));
            var placement = new VisualElement(); root.Add(placement);
            placement.Add(new PropertyField(property.FindPropertyRelative("offset")));
            placement.Add(new PropertyField(property.FindPropertyRelative("yaw")));
            var kind = property.FindPropertyRelative("targets.kind");
            void Refresh(SerializedProperty p) => placement.style.display = TargetQuery.IsVolume((TargetQueryKind)p.intValue) ? DisplayStyle.Flex : DisplayStyle.None;
            Refresh(kind); root.TrackPropertyValue(kind, Refresh);
            return root;
        }
    }
}
