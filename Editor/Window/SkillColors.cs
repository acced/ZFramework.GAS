using UnityEngine;
namespace ZFramework.GAS.Editor
{
    public static class SkillColors
    {
        public static Color Hex(string value) { ColorUtility.TryParseHtmlString(value, out var color); return color; }
        public static readonly Color Ability = Hex("#3d7be0"), Cost = Hex("#d9a43a"), Cooldown = Hex("#2fb3c4"),
            Instant = Hex("#e0803a"), Duration = Hex("#46b36d"), Infinite = Hex("#9a6ce0"),
            Animation = Hex("#d65a9c"), Cue = Hex("#27a596"), Task = Hex("#7f8c9c"),
            Error = Hex("#e5604f"), Warning = Hex("#e7b24a");
    }
}
