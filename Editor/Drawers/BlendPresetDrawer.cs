using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    [CustomPropertyDrawer(typeof(BlendPresetAttribute))]
    public sealed class BlendPresetDrawer:PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root=new VisualElement(); var preset=new EnumField(property.displayName,(SkillBlendCurve)property.enumValueIndex);
            preset.tooltip="混合权重限制在 0–1。Back 曲线保留起停节奏，不产生超出目标姿势的过冲。";preset.BindProperty(property);root.Add(preset);
            string path=property.propertyPath;int dot=path.LastIndexOf('.');
            var custom=property.serializedObject.FindProperty(path.Substring(0,dot+1)+((BlendPresetAttribute)attribute).CustomField);
            var edit=new CurveField("自定义曲线");edit.BindProperty(custom);root.Add(edit);
            var thumbnail=new VisualElement{pickingMode=PickingMode.Ignore,style={height=24,marginLeft=8,marginRight=8}};
            root.Add(thumbnail);thumbnail.generateVisualContent+=ctx=>
            {
                if(!root.visible || property.serializedObject.targetObject==null)return;
                var blend=new SkillBlend(1,(SkillBlendCurve)property.enumValueIndex,custom:custom.animationCurveValue);
                var pen=ctx.painter2D;pen.lineWidth=1.5f;pen.strokeColor=SkillColors.Animation;pen.BeginPath();
                for(int i=0;i<=32;i++){float t=i/32f;var p=new Vector2(t*thumbnail.contentRect.width,22-20*blend.Evaluate(t));if(i==0)pen.MoveTo(p);else pen.LineTo(p);}pen.Stroke();
            };
            void Update(){edit.style.display=property.enumValueIndex==(int)SkillBlendCurve.Custom?DisplayStyle.Flex:DisplayStyle.None;thumbnail.MarkDirtyRepaint();}
            root.TrackPropertyValue(property,_=>Update());root.TrackPropertyValue(custom,_=>Update());Update();return root;
        }
    }
}
