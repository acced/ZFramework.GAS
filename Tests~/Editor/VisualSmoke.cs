using System;
using System.IO;
using System.Linq;
using GameplayTags;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Samples.BasicCombat;
using ZFramework.GAS.Samples.BasicCombat.Editor;

namespace ZFramework.GAS.Editor.Tests
{
    [InitializeOnLoad]
    public static class VisualSmoke
    {
        private static SkillEditorWindow _window;
        private static double _captureAt;
        private static int _stage;
        private static double _pollAt;
        static VisualSmoke() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _pollAt || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _pollAt = EditorApplication.timeSinceStartup + .5;
            string command = Path.Combine(Directory.GetCurrentDirectory(), "Library/GASVisualCommand.txt");
            if (!File.Exists(command)) return;
            string action = File.ReadAllText(command).Trim(); File.Delete(command);
            if (action == "capture") Run();
            else if (action == "quit") EditorApplication.Exit(0);
        }
        public static void Run()
        {
            _stage = 0;
            GameplayTagManager.Initialize(SampleTags.All.Select(name => new GameplayTagRegistration(name)).ToArray());
            var ability = BasicCombatSample.Create();
            _window = SkillEditorWindow.Open(); _window.position = new Rect(0, 28, 1000, 660); _window.Show(ability);
            _window.Document.Playback.Seek(.37f, .9f);
            _window.Document.Selection.Select(ability.Animation, "markers",
                _window.Document.Serialized(ability.Animation).FindProperty("markers").GetArrayElementAtIndex(0).FindPropertyRelative("id").stringValue);
            _captureAt = EditorApplication.timeSinceStartup + 5;
            EditorApplication.update += Capture;
        }
        private static void Capture()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < _captureAt || _window == null) return;
            if (_stage == 0)
            {
                var ability = _window.Document.Root;
                _window.Document.Playback.Seek(.37f, .9f);
                _window.Document.Selection.Select(ability.Animation, "markers",
                    _window.Document.Serialized(ability.Animation).FindProperty("markers").GetArrayElementAtIndex(0).FindPropertyRelative("id").stringValue);
                _window.rootVisualElement.Q<SkillGraphView>().AutoArrange();
                _stage++; _captureAt = EditorApplication.timeSinceStartup + 3; return;
            }
            try
            {
                _window.Repaint();
                string output = Path.Combine(Directory.GetCurrentDirectory(), "Temp/GASVisual");
                Directory.CreateDirectory(output);
                float scale = EditorGUIUtility.pixelsPerPoint; Rect rect = _window.position;
                int width = Mathf.RoundToInt(rect.width * scale), height = Mathf.RoundToInt(rect.height * scale);
                var pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(rect.position, width, height);
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.SetPixels(pixels); texture.Apply(); File.WriteAllBytes(output + "/skill-editor.png", texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
                string Describe(VisualElement e, int depth)
                {
                    if (depth > 5) return "";
                    return new string(' ', depth * 2) + e.GetType().Name + "#" + e.name + " " + e.layout + " " + e.resolvedStyle.display + "\n" +
                        string.Concat(e.Children().Select(c => Describe(c, depth + 1)));
                }
                File.WriteAllText(output + "/layout.txt", Describe(_window.rootVisualElement, 0));
                using (var validation = new ValidationService(_window.Document))
                {
                    validation.ValidateNow(true);
                    File.WriteAllText(output + "/issues.txt", string.Join("\n", validation.Issues.Select(i => i.Id + " " + i.Message)));
                }
                File.WriteAllText(output + "/complete.txt", "Visual smoke captured " + DateTime.UtcNow.ToString("O"));
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { EditorApplication.update -= Capture; }
        }
    }
}
