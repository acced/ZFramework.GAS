using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(TargetQuery))]
    public sealed class TargetQueryDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new Foldout { text = property.displayName, value = true };
            PropertyField Field(string name, string label)
            {
                var field = new PropertyField(property.FindPropertyRelative(name), label);
                root.Add(field); return field;
            }
            bool filterOnly = fieldInfo?.IsDefined(typeof(TargetFiltersOnlyAttribute), true) == true;
            var kind = property.FindPropertyRelative("kind");
            if (!filterOnly) Field("kind", "命中来源");
            else root.Add(new Label("命中由投射物扫掠产生。"));
            var key = Field("sourceKey", "来源键");
            var hint = new HelpBox("", HelpBoxMessageType.Info); root.Add(hint);
            var origin = Field("origin", "查询原点"); var radius = Field("radius", "半径 / 长度");
            var angle = Field("angle", "扇形角度"); var width = Field("width", "宽"); var height = Field("height", "高");
            var layer = Field("layerMask", "目标碰撞层");
            Field("relation", "目标关系"); var self = Field("excludeSelf", "排除自身"); var limit = Field("maxTargets", "最大目标数");
            Field("requiredTags", "必需标签"); Field("anyTags", "任一标签"); Field("blockedTags", "排除标签");
            void Refresh(SerializedProperty p)
            {
                var mode = (TargetQueryKind)p.enumValueIndex;
                bool volume = !filterOnly && TargetQuery.IsVolume(mode);
                void Show(VisualElement e, bool show) => e.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                Show(key, !filterOnly && mode is TargetQueryKind.Hitbox or TargetQueryKind.Project);
                key.label = mode == TargetQueryKind.Hitbox ? "碰撞体槽位" : "项目来源键";
                hint.text = mode switch {
                    TargetQueryKind.Hitbox => "在单位的 Skill Hitboxes 组件绑定武器 Collider，填写相同的槽位名。",
                    TargetQueryKind.Project => "由单位接入的 ISkillTargetSource 提供对象；几何预演不执行项目逻辑。",
                    TargetQueryKind.FixedTarget => "使用施放或当前分支指定的单位，不需要 Collider。", _ => "" };
                Show(hint, !filterOnly && hint.text.Length > 0);
                Show(origin, volume && mode != TargetQueryKind.TargetPoint); Show(radius, volume);
                Show(width, volume && mode == TargetQueryKind.Line); Show(height, volume && mode == TargetQueryKind.Line);
                Show(angle, volume && mode == TargetQueryKind.Sector);
                Show(layer, filterOnly || volume || mode == TargetQueryKind.Hitbox);
                Show(self, filterOnly || mode != TargetQueryKind.Self);
                Show(limit, !filterOnly && mode is not (TargetQueryKind.FixedTarget or TargetQueryKind.Self));
            }
            Refresh(kind); root.TrackPropertyValue(kind, Refresh);
            return root;
        }
    }
}
