using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public abstract class SkillTemplate
    {
        public abstract string DisplayName { get; }
        public abstract void Build(GameplayAbilityAsset ability);
    }
    public static class SkillTemplateRegistry
    {
        public static IReadOnlyList<SkillTemplate> Templates => TypeCache.GetTypesDerivedFrom<SkillTemplate>()
            .Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null).Select(t => (SkillTemplate)Activator.CreateInstance(t)).OrderBy(t => t.DisplayName).ToArray();
        public static GameplayAbilityAsset Create(string path, SkillTemplate template)
        {
            var ability = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); ability.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(ability, path);
            Undo.RegisterCreatedObjectUndo(ability, "新建技能");
            template.Build(ability); EditorUtility.SetDirty(ability); AssetDatabase.SaveAssetIfDirty(ability);
            SkillIndex.Invalidate(); return ability;
        }
        public static void Choose(SkillTemplate template, Action<GameplayAbilityAsset> open)
        {
            string path = EditorUtility.SaveFilePanelInProject("新建技能 · " + template.DisplayName, template.DisplayName, "asset", "选择技能资产的保存位置");
            if (string.IsNullOrEmpty(path)) return;
            int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("新建技能「" + template.DisplayName + "」");
            var ability = Create(path, template); Undo.CollapseUndoOperations(group); open(ability);
        }
    }

    // The base editor can always create an ability, even without any presentation templates.
    public sealed class EmptySkillTemplate : SkillTemplate
    {
        public override string DisplayName => "空白技能";
        public override void Build(GameplayAbilityAsset ability) { }
    }
}
