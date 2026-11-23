using System.IO;
using IronOath.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IronOath.Editor
{
    internal static class IronOathDemoEditor
    {
        [MenuItem("Tools/Iron Oath/Open Demo")]
        static void OpenDemo()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string[] guids = AssetDatabase.FindAssets("IronOathDemo t:Scene");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path) != "IronOathDemo") continue;
                EditorSceneManager.OpenScene(path);
                Selection.activeObject = Object.FindFirstObjectByType<IronOathDemo>();
                return;
            }
            // Importing the optional sample normally provides the scene. This also supports a source-only copy.
            const string folder = "Assets/IronOath/Scenes";
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Iron Oath - Demo", typeof(IronOathDemo));
            string output = AssetDatabase.GenerateUniqueAssetPath(folder + "/IronOathDemo.unity");
            EditorSceneManager.SaveScene(scene, output);
            Selection.activeObject = root;
        }
    }
}
