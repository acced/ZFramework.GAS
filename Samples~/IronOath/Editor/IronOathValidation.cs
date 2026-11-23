using System;
using System.Collections.Generic;
using System.IO;
using IronOath.Combat;
using IronOath.Presentation;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IronOath.Editor
{
    /// <summary>Read-only import checks. Preview scenes are always closed; project settings are never changed.</summary>
    public static class IronOathValidation
    {
        static readonly string[] ArtNames = { "BlackstoneCourtyard", "PawnshopInterior", "BlackstoneMap" };

        public static string FindDemoScenePath()
        {
            string[] guids = AssetDatabase.FindAssets("IronOathDemo t:Scene", new[] { "Assets" });
            string match = null;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path) != "IronOathDemo") continue;
                if (match != null) throw new InvalidOperationException("More than one imported IronOathDemo scene was found. Keep one sample import in the validation project.");
                match = path;
            }
            return match;
        }

        [MenuItem("Tools/Iron Oath/Validate Imported Demo")]
        public static void ValidateImportedDemo()
        {
            string[] errors = CollectErrors();
            if (errors.Length == 0) Debug.Log("Iron Oath import checks passed. Run the PlayMode tests and inspect the Game view before accepting rendering.");
            else for (int i = 0; i < errors.Length; i++) Debug.LogError("Iron Oath: " + errors[i]);
        }

        [MenuItem("Tools/Iron Oath/Validate Current Scene")]
        public static void ValidateCurrentScene()
        {
            var errors = new List<string>();
            ValidateScene(SceneManager.GetActiveScene(), errors);
            if (errors.Count == 0) Debug.Log("Iron Oath current scene checks passed. No assets or project settings were modified.");
            else for (int i = 0; i < errors.Count; i++) Debug.LogError("Iron Oath: " + errors[i]);
        }

        // Unity -batchmode -quit -projectPath <project> -executeMethod IronOath.Editor.IronOathValidation.ValidateBatch
        public static void ValidateBatch()
        {
            string[] errors = CollectErrors();
            if (errors.Length != 0) throw new BuildFailedException(string.Join("\n", errors));
            Debug.Log("PASS: Iron Oath imported scene, resources, entry point and actor bindings.");
        }

        public static string[] CollectErrors()
        {
            var errors = new List<string>();
            string path;
            try { path = FindDemoScenePath(); }
            catch (Exception exception) { errors.Add(exception.Message); return errors.ToArray(); }
            if (string.IsNullOrEmpty(path))
            {
                errors.Add("Import the Iron Oath Pawnshop sample before validating it.");
                return errors.ToArray();
            }
            for (int i = 0; i < ArtNames.Length; i++)
            {
                Texture2D texture = Resources.Load<Texture2D>("IronOath/Art/" + ArtNames[i]);
                if (texture == null) errors.Add("Missing imported environment: " + ArtNames[i]);
                else if (texture.width < 512 || texture.height < 256) errors.Add("Environment texture imported at an unexpectedly small size: " + ArtNames[i]);
            }
            Font font = Resources.Load<Font>("IronOath/Fonts/IronOathSerif");
            if (font == null) errors.Add("Missing included Chinese font: IronOath/Fonts/IronOathSerif");
            else
            {
                const string required = "铁誓当铺雷恩克萝伊护甲生命";
                for (int i = 0; i < required.Length; i++)
                    if (!font.HasCharacter(required[i])) errors.Add("The included font is missing the character: " + required[i]);
            }
            Shader shader = Resources.Load<Shader>("IronOath/WorldUnlit");
            if (shader == null) errors.Add("Missing world shader: IronOath/WorldUnlit");
            else if (ShaderUtil.ShaderHasError(shader)) errors.Add("Iron Oath world shader has import or compilation errors; inspect its compiler messages.");
            Scene preview = EditorSceneManager.OpenPreviewScene(path);
            try { ValidateScene(preview, errors); }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
            return errors.ToArray();
        }

        static void ValidateScene(Scene scene, List<string> errors)
        {
            if (!scene.IsValid() || !scene.isLoaded) { errors.Add("The scene is not loaded."); return; }
            GameObject[] roots = scene.GetRootGameObjects();
            int entries = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                CheckMissingScripts(roots[i], errors, "Scene");
                IronOathDemo[] demos = roots[i].GetComponentsInChildren<IronOathDemo>(true);
                for (int j = 0; j < demos.Length; j++)
                {
                    entries++;
                    if (!demos[j].enabled || !demos[j].gameObject.activeInHierarchy) errors.Add("IronOathDemo must be enabled on an active GameObject.");
                    if (demos[j].GetComponentInParent<Canvas>() != null) errors.Add("IronOathDemo must live outside a Canvas so world actors remain world objects.");
                    ValidateActors(demos[j], errors);
                    ValidateWorldLayer(demos[j], roots, errors);
                }
            }
            if (entries != 1) errors.Add("Expected exactly one IronOathDemo entry point; found " + entries + ".");
        }

        static void ValidateWorldLayer(IronOathDemo demo, GameObject[] roots, List<string> errors)
        {
            var serialized = new SerializedObject(demo);
            SerializedProperty layerProperty = serialized.FindProperty("battleLayer");
            if (layerProperty == null) { errors.Add("The demo does not expose its battle camera layer."); return; }
            int layer = layerProperty.intValue;
            if ((uint)layer > 31) { errors.Add("Battle Layer must be between 0 and 31."); return; }
            for (int root = 0; root < roots.Length; root++)
            {
                Renderer[] renderers = roots[root].GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i].gameObject.layer == layer && !renderers[i].transform.IsChildOf(demo.transform))
                        errors.Add("Battle Layer " + layer + " is also used by scene renderer " + renderers[i].name +
                            ". Select an unused layer for the sample camera.");
            }
        }

        static void ValidateActors(IronOathDemo demo, List<string> errors)
        {
            var serialized = new SerializedObject(demo);
            SerializedProperty actors = serialized.FindProperty("actors");
            var definitions = new HashSet<string>(StringComparer.Ordinal);
            for (int encounter = 0; encounter < 3; encounter++)
            {
                BattleSimulation battle = BattleCatalog.CreateDemo(encounterIndex: encounter);
                for (int unit = 0; unit < battle.UnitCount; unit++) definitions.Add(battle.GetUnit(unit).Definition.Id);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; actors != null && i < actors.arraySize; i++)
            {
                SerializedProperty actor = actors.GetArrayElementAtIndex(i);
                string id = actor.FindPropertyRelative("unitDefinitionId").stringValue ?? "";
                string label = "Actors[" + i + "] (" + (id.Length == 0 ? "fallback" : id) + "): ";
                if (!seen.Add(id)) errors.Add(label + "duplicate unit or fallback binding.");
                if (id.Length > 0 && !definitions.Contains(id)) errors.Add(label + "unknown unitDefinitionId.");
                float scale = actor.FindPropertyRelative("worldScale").floatValue;
                if (!float.IsFinite(scale) || scale <= 0) errors.Add(label + "worldScale must be finite and greater than zero.");
                Vector3 offset = actor.FindPropertyRelative("footOffset").vector3Value;
                if (!Finite(offset)) errors.Add(label + "footOffset must be finite.");
                var prefab = actor.FindPropertyRelative("prefab").objectReferenceValue as GameObject;
                if (prefab == null) { errors.Add(label + "assign a world-space prefab or remove the unused entry."); continue; }
                if (!PrefabUtility.IsPartOfPrefabAsset(prefab)) errors.Add(label + "use a prefab asset rather than a scene object.");
                if (prefab.GetComponentsInChildren<Canvas>(true).Length != 0 || prefab.GetComponentsInChildren<RectTransform>(true).Length != 0)
                    errors.Add(label + "use world-space renderers; Canvas, SkeletonGraphic and UI transforms do not belong in an actor prefab.");
                if (prefab.GetComponentsInChildren<Renderer>(true).Length == 0) errors.Add(label + "prefab has no world-space Renderer.");
                if (prefab.GetComponentsInChildren<Camera>(true).Length != 0) errors.Add(label + "actor prefabs must not contain cameras.");
                Vector3 authoredScale = prefab.transform.localScale;
                if (!Finite(authoredScale) || authoredScale.x == 0 || authoredScale.y == 0 || authoredScale.z == 0)
                    errors.Add(label + "prefab scale must be finite and nonzero.");
                CheckMissingScripts(prefab, errors, label);
            }
        }

        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        static void CheckMissingScripts(GameObject root, List<string> errors, string label)
        {
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(children[i].gameObject) != 0)
                    errors.Add(label + " missing script on " + children[i].name + ".");
        }
    }
}
