using System;
using UnityEditor;
using UnityEngine;

namespace ZFramework.GAS.Editor
{
    public static class EditCommand
    {
        private static int _depth;
        public static void Run(SkillDocument document, string name, UnityEngine.Object asset,
            Action<SerializedObject> edit, bool structure = false, params string[] paths)
            => Transaction(document, name, asset, () =>
            {
                using var so = new SerializedObject(asset);
                so.Update(); edit(so); so.ApplyModifiedProperties();
            }, structure, paths);

        public static void Transaction(SkillDocument document, string name, UnityEngine.Object asset,
            Action action, bool structure = true, params string[] paths)
        {
            // A multi-selection gesture can call existing commands without creating extra Undo steps.
            if (_depth > 0) { action(); return; }
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            _depth++;
            try { action(); document?.Notify(new ChangeSet(asset, structure, paths)); Undo.FlushUndoRecordObjects(); }
            catch { Undo.RevertAllDownToGroup(group); document?.Notify(new ChangeSet(asset, true)); throw; }
            finally { _depth--; Undo.SetCurrentGroupName(name); Undo.CollapseUndoOperations(group); }
        }
    }
}
