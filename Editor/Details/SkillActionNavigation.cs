using UnityEngine;
using UnityEditor;

namespace ZFramework.GAS.Editor
{
    public static class SkillActionNavigation
    {
        public static void Select(SkillDocument document, Issue issue)
        {
            string track = issue.Track ?? (string.IsNullOrEmpty(issue.PropertyPath) ? null : "@field");
            document.Selection.Select(issue.Asset, track, issue.ElementId ?? issue.PropertyPath);
            if (track == "@field" && issue.PropertyPath?.IndexOf("ctions.Array.data[", System.StringComparison.Ordinal) >= 0)
                document.OpenFlow();
        }

        public static bool Select(SkillDocument document, string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return false;
            foreach (var asset in document.Assets)
            {
                var property = document.Serialized(asset).GetIterator();
                while (property.Next(true))
                    if (property.name == "id" && property.propertyType == SerializedPropertyType.String && property.stringValue == actionId)
                    {
                        string path = property.propertyPath;
                        document.Selection.Select(asset, "@field", path.Substring(0, path.LastIndexOf('.')));
                        return true;
                    }
            }
            return false;
        }
    }
}
