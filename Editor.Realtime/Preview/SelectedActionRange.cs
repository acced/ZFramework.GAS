using UnityEditor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Resolve only the selected query, at edit/selection time. Inspecting a radius must not run a cast.
    internal static class SelectedActionRange
    {
        internal static TargetShape? Read(SkillDocument doc, int level, out SkillOrigin origin, out string note)
        {
            origin = SkillOrigin.Caster; note = null;
            SelectionModel sel = doc.Selection;
            if (doc.Root == null || sel.Asset == null || sel.ElementId == null) return null;

            SerializedObject so = doc.Serialized(sel.Asset);
            TargetQuery query = null;
            if (sel.Track == "hitWindows" && sel.Asset is SkillAnimationAsset)
                query = TimelineCommands.Find(so, "hitWindows", sel.ElementId)?.FindPropertyRelative("targets")?.boxedValue as TargetQuery;
            else if (sel.Track == "@field")
            {
                for (string path = sel.ElementId; path.Length > 0;)
                {
                    SerializedProperty f = so.FindProperty(path);
                    if (f?.propertyType == SerializedPropertyType.ManagedReference && f.managedReferenceValue is SkillAction)
                    { query = FindQuery(f); break; }
                    int dot = path.LastIndexOf('.');
                    if (dot < 0) break;
                    path = path.Substring(0, dot);
                }
            }
            else if (sel.Track == "markers" && sel.Asset is SkillAnimationAsset)
            {
                SerializedProperty list = TimelineCommands.Find(so, "markers", sel.ElementId)?.FindPropertyRelative("actions");
                if (list != null)
                    for (int i = 0; i < list.arraySize && query == null; i++) query = FindQuery(list.GetArrayElementAtIndex(i));
            }

            if (query == null) return null;
            if (query.kind == TargetQueryKind.Project) { note = "目标由项目提供；几何预演不会运行项目逻辑。"; return null; }
            if (!TargetQuery.IsVolume(query.kind)) return null;
            TargetQueryDefinition def = query.Build();
            origin = def.EffectiveOrigin;
            if (origin == SkillOrigin.EventPosition) { note = "范围原点由实际事件提供。"; return null; }
            return def.ResolveShape(new EffectEvaluationContext(level, doc.Root.ResolveParameters(level, null)));
        }

        private static TargetQuery FindQuery(SerializedProperty action)
        {
            if (action == null) return null;
            using SerializedProperty field = action.Copy();
            SerializedProperty end = field.GetEndProperty();
            if (!field.Next(true)) return null;
            do
            {
                if (SerializedProperty.EqualContents(field, end)) break;
                if (field.type == nameof(TargetQuery) && field.boxedValue is TargetQuery found) return found;
            }
            while (field.Next(false));
            return null;
        }
    }
}
