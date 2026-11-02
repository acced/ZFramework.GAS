using System.Collections.Generic;
using UnityEditor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [InitializeOnLoad]
    public static class SkillActionDebugger
    {
        private static readonly HashSet<string> Breakpoints = new();
        public static string PausedActionId { get; private set; }
        public static bool PauseNext;
        static SkillActionDebugger()
        {
            SkillActionDebug.Boundary += Boundary;
            EditorApplication.playModeStateChanged += _ => { PausedActionId = null; PauseNext = false; };
        }
        public static void Toggle(string actionId)
        { if (!string.IsNullOrEmpty(actionId) && !Breakpoints.Remove(actionId)) Breakpoints.Add(actionId); }
        private static void Boundary(SkillExecutionContext context, SkillActionDefinition action)
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) return;
            if (!PauseNext && !Breakpoints.Contains(action.Id)) return;
            PauseNext = false; PausedActionId = action.Id;
            EditorApplication.isPaused = true;
        }
        public static void StepScene()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.isPaused = true; EditorApplication.Step();
        }
    }
}
