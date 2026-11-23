using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZFramework.GAS.Editor
{
    public static class EditCommand
    {
        private static int _depth;
        private static bool _nestedStructure;
        private static readonly List<string> _nestedPaths = new();

        public static void Run(SkillDocument document, string name, UnityEngine.Object asset,
            Action<SerializedObject> edit, bool structure = false, params string[] paths)
            => Transaction(document, name, asset, () =>
            {
                using var so = new SerializedObject(asset);
                so.Update(); edit(so); so.ApplyModifiedProperties();
            }, structure, paths);

        internal static void Cleanup(Action step) { try { step(); } catch (Exception error) { Debug.LogException(error); } }

        public static void Transaction(SkillDocument document, string name, UnityEngine.Object asset,
            Action action, bool structure = true, params string[] paths)
        {
            if (_depth > 0)
            {
                _nestedStructure |= structure || paths == null || paths.Length == 0;
                if (paths != null) foreach (string p in paths) if (!string.IsNullOrEmpty(p)) _nestedPaths.Add(p);
                action();
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            _depth++;
            _nestedStructure = structure;
            _nestedPaths.Clear();
            if (paths != null) foreach (string p in paths) if (!string.IsNullOrEmpty(p)) _nestedPaths.Add(p);

            bool succeeded = false;
            try
            {
                action();
                document?.Notify(new ChangeSet(asset, _nestedStructure, _nestedPaths.Count > 0 ? _nestedPaths.ToArray() : Array.Empty<string>()));
                Undo.FlushUndoRecordObjects();
                succeeded = true;
            }
            catch
            {
                Cleanup(() => Undo.RevertAllDownToGroup(group));
                Cleanup(() => document?.Notify(new ChangeSet(asset, true)));
                throw;
            }
            finally
            {
                _depth--; _nestedStructure = false; _nestedPaths.Clear();
                void Finish() { Undo.SetCurrentGroupName(name); Undo.CollapseUndoOperations(group); }
                if (succeeded) Finish(); else Cleanup(Finish);
            }
        }
    }
}
