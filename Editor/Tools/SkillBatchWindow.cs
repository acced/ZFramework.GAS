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
    public sealed class SkillBatchWindow : EditorWindow
    {
        [MenuItem("Window/ZFramework GAS/技能批量配置")]
        public static void Open() => GetWindow<SkillBatchWindow>("技能批量配置");
        private void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.Add(new HelpBox("每行修改支持 Undo。共享效果会影响全部引用者；参数化冷却写入当前技能的参数覆盖。", HelpBoxMessageType.Info));
            var scroll = new ScrollView(); rootVisualElement.Add(scroll);
            foreach (var ability in SkillIndex.Abilities)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row } }; scroll.Add(row);
                row.Add(new Button(() => SkillEditorWindow.Open().Show(ability)) { text = ability.name, style = { width = 160 } });
                var so = new SerializedObject(ability);
                var name = new TextField("显示名") { style = { width = 280 } }; name.BindProperty(so.FindProperty("displayName")); row.Add(name);
                var cooldown = so.FindProperty("cooldown").objectReferenceValue as GameplayEffectAsset;
                if (cooldown == null) { row.Add(new Label("无冷却")); continue; }
                using var effect = new SerializedObject(cooldown);
                float value = effect.FindProperty("duration").floatValue;
                if (effect.FindProperty("useDurationMagnitude").boolValue)
                {
                    try { value = new GameplayEffectSpec(cooldown.Definition, context: new EffectEvaluationContext(1, ability.ResolveParameters(1))).Resolve(cooldown.Definition.DurationMagnitude.Value, null); }
                    catch { }
                }
                var seconds = new FloatField("冷却 / s") { value = value, isDelayed = true, style = { width = 230 } }; row.Add(seconds);
                seconds.RegisterValueChangedCallback(e => { SetCooldown(ability, Mathf.Max(0, e.newValue)); });
            }
        }
        public static void SetCooldown(GameplayAbilityAsset ability, float seconds)
        {
            using var owner = new SerializedObject(ability);
            var effect = owner.FindProperty("cooldown").objectReferenceValue as GameplayEffectAsset; if (effect == null) return;
            using var so = new SerializedObject(effect);
            if (so.FindProperty("durationAttributeId").intValue >= 0) throw new InvalidOperationException("冷却取自来源属性，请修改对应属性配置。");
            if (so.FindProperty("useDurationMagnitude").boolValue)
            {
                if (so.FindProperty("durationMagnitude.kind").enumValueIndex != (int)MagnitudeKind.Parameter)
                    throw new InvalidOperationException("此冷却使用等级表或公式，请在 Details 编辑。");
                string id = so.FindProperty("durationMagnitude.parameterId").stringValue;
                float coefficient = so.FindProperty("durationMagnitude.coefficient").floatValue;
                if (coefficient == 0) throw new InvalidOperationException("冷却参数系数为零。");
                float value = (seconds - so.FindProperty("durationMagnitude.constant").floatValue) / coefficient;
                Undo.RecordObject(ability, "批量配置冷却");
                var list = ability.parameterOverrides.ToList(); list.RemoveAll(p => p.parameterId == id);
                list.Add(new SkillParameterOverride { parameterId = id, value = value }); ability.parameterOverrides = list.ToArray();
                EditorUtility.SetDirty(ability); ability.InvalidateDefinition();
            }
            else { so.FindProperty("duration").floatValue = seconds; so.ApplyModifiedProperties(); effect.InvalidateDefinition(); ability.InvalidateDefinition(); }
        }
    }
}

