using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public static class BasicCombatSample
    {
        public const string Folder = "Assets/GAS Samples/BasicCombat";
        [MenuItem("Tools/ZFramework GAS/创建 BasicCombat 示例")]
        public static void CreateAndOpen() => SkillEditorWindow.Open().Show(Create());
        public static GameplayAbilityAsset Create()
        {
            EnsureFolder(Folder);
            if (AssetDatabase.LoadAssetAtPath<AttributeCatalogAsset>(Folder + "/Attributes.asset") == null)
            {
                var catalog = ScriptableObject.CreateInstance<AttributeCatalogAsset>();
                catalog.entries = new[] { Entry(1, "生命"), Entry(2, "最大生命"), Entry(3, "攻击"), Entry(4, "体力") };
                AssetDatabase.CreateAsset(catalog, Folder + "/Attributes.asset"); AttributeCatalog.Invalidate();
            }
            var preview = CreateCharacter(); GameplayAbilityAsset melee = null;
            foreach (var template in SkillTemplateRegistry.Templates)
            {
                string name = template is MeleeTemplate ? "普攻_剑士" : template.DisplayName;
                string path = Folder + "/" + name + ".asset";
                var ability = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(path);
                if (ability == null)
                {
                    ability = SkillTemplateRegistry.Create(path, template);
                    using var animation = new SerializedObject(ability.Animation);
                    animation.FindProperty("clipName").stringValue = "Base Layer.Attack01";
                    animation.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssetIfDirty(ability);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var layout = new LayoutStore(ability); layout.PreviewPrefab = preview; layout.Save();
                }
                if (template is MeleeTemplate) melee = ability;
            }
            using (var animation = new SerializedObject(melee.Animation))
            {
                if (animation.FindProperty("cueRegions").arraySize == 0)
                {
                    var cues = animation.FindProperty("cueRegions"); cues.arraySize = 1; var cue = cues.GetArrayElementAtIndex(0);
                    new CueRegionTrack().InitializeNew(cue, .12f, null); cue.FindPropertyRelative("end").floatValue = .65f; cue.FindPropertyRelative("cueTag.m_Name").stringValue = "Cue.Sword.Trail";
                    var windows = animation.FindProperty("tagWindows"); windows.arraySize = 2;
                    for (int i = 0; i < 2; i++)
                    {
                        var window = windows.GetArrayElementAtIndex(i); new TagWindowTrack().InitializeNew(window, i == 0 ? .1f : .48f, null);
                        window.FindPropertyRelative("end").floatValue = i == 0 ? .43f : .76f;
                        var tags = window.FindPropertyRelative("tags"); tags.arraySize = 1;
                        tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = i == 0 ? "State.SuperArmor" : "Window.Combo";
                    }
                    animation.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            AssetDatabase.SaveAssets(); SkillIndex.Invalidate(); return melee;
        }
        private static AttributeCatalogEntry Entry(int id, string name) => new() { id = id, displayName = name, category = "战斗" };
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        private static GameObject CreateCharacter()
        {
            string path = Folder + "/Swordsman.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path); if (existing != null) return existing;
            var steel = MakeMaterial("Steel", "#879bae"); var dark = MakeMaterial("Cloth", "#34465f"); var gold = MakeMaterial("Gold", "#e7b75e");
            var root = new GameObject("Swordsman");
            try
            {
                Part(root.transform, "Body", PrimitiveType.Capsule, new Vector3(0, 1.1f, 0), new Vector3(.48f, .48f, .32f), steel);
                Part(root.transform, "Head", PrimitiveType.Sphere, new Vector3(0, 1.78f, 0), Vector3.one * .35f, steel);
                Part(root.transform, "Visor", PrimitiveType.Cube, new Vector3(0, 1.8f, -.16f), new Vector3(.27f, .07f, .05f), dark);
                Part(root.transform, "LeftLeg", PrimitiveType.Capsule, new Vector3(-.15f, .4f, 0), new Vector3(.18f, .3f, .18f), dark);
                Part(root.transform, "RightLeg", PrimitiveType.Capsule, new Vector3(.15f, .4f, 0), new Vector3(.18f, .3f, .18f), dark);
                var arm = new GameObject("SwordArm"); arm.transform.SetParent(root.transform, false); arm.transform.localPosition = new Vector3(.38f, 1.45f, 0);
                Part(arm.transform, "Arm", PrimitiveType.Capsule, new Vector3(0, -.21f, 0), new Vector3(.16f, .23f, .16f), steel);
                Part(arm.transform, "Blade", PrimitiveType.Cube, new Vector3(0, -.55f, -.42f), new Vector3(.07f, .07f, .86f), gold);
                Part(root.transform, "Shield", PrimitiveType.Cube, new Vector3(-.42f, 1.15f, -.14f), new Vector3(.34f, .55f, .12f), dark);
                var clip = new AnimationClip { name = "Attack01", frameRate = 30 };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("SwordArm", typeof(Transform), "localEulerAnglesRaw.x"),
                    new AnimationCurve(new Keyframe(0, 0), new Keyframe(.2f, -110), new Keyframe(.36f, 50), new Keyframe(.6f, 20), new Keyframe(.8f, 0)));
                AssetDatabase.CreateAsset(clip, Folder + "/Attack01.anim");
                var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/Swordsman.controller");
                var state = controller.layers[0].stateMachine.AddState("Attack01"); state.motion = clip; controller.layers[0].stateMachine.defaultState = state;
                root.AddComponent<Animator>().runtimeAnimatorController = controller; root.AddComponent<SkillAnimator>();
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { Object.DestroyImmediate(root); }
        }
        private static Material MakeMaterial(string name, string hex)
        {
            string path = Folder + "/" + name + ".mat"; var existing = AssetDatabase.LoadAssetAtPath<Material>(path); if (existing != null) return existing;
            var material = new Material(Shader.Find("Standard")) { color = SkillColors.Hex(hex), name = name };
            AssetDatabase.CreateAsset(material, path); return material;
        }
        private static void Part(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(type); part.name = name; part.transform.SetParent(parent, false);
            part.transform.localPosition = position; part.transform.localScale = scale; part.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(part.GetComponent<Collider>());
        }
    }
}
