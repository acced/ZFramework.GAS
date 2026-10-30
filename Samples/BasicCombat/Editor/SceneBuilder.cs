using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>可游玩的训练场：石台、立柱旗帜、火盆、骑士与训练假人，以及接好线的 <see cref="TrainingArena"/>。</summary>
    internal static class SceneBuilder
    {
        public static string Build(string path, SampleArt art, CharacterBuilder.Palette m, GameObject knightPrefab, GameObject dummyPrefab, GameplayAbilityAsset[] skills, Scene replacementScene = default)
        {
            // 已保存的场景保持不动：以附加方式建场景，保存后关闭。
            // 存在未命名场景时 Unity 不允许附加新建，只能单场景新建，生成后停留在训练场。
            var previous = SceneManager.GetActiveScene();
            bool prepared = replacementScene.IsValid();
            bool additive = !prepared;
            for (int i = 0; i < SceneManager.sceneCount; i++) if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path)) additive = false;
            if (!prepared && !additive && !Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return null;
            // 单场景新建会卸载未被引用的资产：先记下路径，建好场景后重新取回。
            string knightPath = AssetDatabase.GetAssetPath(knightPrefab), dummyPath = AssetDatabase.GetAssetPath(dummyPrefab);
            var skillPaths = System.Array.ConvertAll(skills, AssetDatabase.GetAssetPath);
            var materialNames = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var field in typeof(CharacterBuilder.Palette).GetFields())
                if (field.GetValue(m) is Material material) materialNames[field.Name] = material.name;
            var scene = prepared ? replacementScene : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, additive ? NewSceneMode.Additive : NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            art.Refresh();
            m = new CharacterBuilder.Palette();
            foreach (var pair in materialNames)
                typeof(CharacterBuilder.Palette).GetField(pair.Key).SetValue(m, AssetDatabase.LoadAssetAtPath<Material>(art.Folder + "/" + pair.Value + ".mat"));
            knightPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(knightPath);
            dummyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(dummyPath);
            skills = System.Array.ConvertAll(skillPaths, AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>);
            try
            {
                Lighting();
                var environment = new GameObject("Environment").transform;
                Arena(art, m, environment);

                var hero = (GameObject)PrefabUtility.InstantiatePrefab(knightPrefab, scene);
                hero.name = "Knight"; hero.transform.position = new Vector3(0, 0, -1.3f);
                var heroCollider = hero.AddComponent<CapsuleCollider>(); heroCollider.center = new Vector3(0, .9f, 0); heroCollider.height = 1.8f; heroCollider.radius = .35f;
                var heroUnit = Unit(hero, "骑士", 300, 20, new Vector3(0, 2.15f, 0));

                var dummy = (GameObject)PrefabUtility.InstantiatePrefab(dummyPrefab, scene);
                dummy.name = "TrainingDummy"; dummy.transform.SetPositionAndRotation(new Vector3(.1f, 0, .5f), Quaternion.Euler(0, 180, 0));
                var dummyCollider = dummy.AddComponent<CapsuleCollider>(); dummyCollider.center = new Vector3(0, .85f, 0); dummyCollider.height = 1.7f; dummyCollider.radius = .38f;
                var dummyUnit = Unit(dummy, "训练假人", 600, 0, new Vector3(0, 1.75f, 0));
                var aim = SampleArt.Bone(dummy.transform, "HitPoint", new Vector3(0, 1.0f, 0));

                var camera = new GameObject("Main Camera") { tag = "MainCamera" };
                var cam = camera.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = SampleArt.Hex("#0e121a"); cam.fieldOfView = 38;
                camera.transform.position = new Vector3(3.7f, 2.1f, .3f);
                camera.transform.LookAt(new Vector3(-.3f, .9f, -.35f));
                camera.AddComponent<AudioListener>();

                var arena = new GameObject("TrainingArena").AddComponent<TrainingArena>();
                var guardian = art.Additive("Vfx_Guardian", null, "#ffffff");
                using (var so = new SerializedObject(arena))
                {
                    so.FindProperty("hero").objectReferenceValue = heroUnit;
                    so.FindProperty("dummy").objectReferenceValue = dummyUnit;
                    so.FindProperty("castPoint").objectReferenceValue = Find(hero.transform, "SwordTip");
                    so.FindProperty("aimPoint").objectReferenceValue = aim;
                    so.FindProperty("burstMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(art.Folder + "/Vfx_Glow.mat");
                    so.FindProperty("burstMesh").objectReferenceValue = FindMesh(art, "Vfx.GlowCross");
                    so.FindProperty("guardianPrefab").objectReferenceValue = knightPrefab;
                    so.FindProperty("guardianMaterial").objectReferenceValue = guardian;
                    so.FindProperty("summonPoint").vector3Value = KnightAnimations.Timing.SummonPoint;
                    string[] colors = { "#ffb347", "#4fe3ff", "#ff7a45", "#5fe8ff", "#c46bff" };
                    var list = so.FindProperty("skills"); list.arraySize = skills.Length;
                    for (int i = 0; i < skills.Length; i++)
                    {
                        var item = list.GetArrayElementAtIndex(i);
                        item.FindPropertyRelative("displayName").stringValue = skills[i].name;
                        item.FindPropertyRelative("asset").objectReferenceValue = skills[i];
                        item.FindPropertyRelative("color").colorValue = SampleArt.Hex(colors[i % colors.Length]);
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorSceneManager.SaveScene(scene, path);
                return path;
            }
            finally
            {
                if (additive)
                {
                    if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static CombatUnit Unit(GameObject go, string name, float health, float attack, Vector3 hud)
        {
            var unit = go.AddComponent<CombatUnit>();
            var anchor = SampleArt.Bone(go.transform, "HudAnchor", hud);
            using var so = new SerializedObject(unit);
            so.FindProperty("displayName").stringValue = name;
            so.FindProperty("maxHealth").floatValue = health;
            so.FindProperty("attack").floatValue = attack;
            so.FindProperty("hudAnchor").objectReferenceValue = anchor;
            so.ApplyModifiedPropertiesWithoutUndo();
            return unit;
        }

        private static void Lighting()
        {
            var sun = new GameObject("Key Light").AddComponent<Light>();
            sun.type = LightType.Directional; sun.color = SampleArt.Hex("#ffe6c7"); sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft; sun.shadowStrength = .72f;
            sun.transform.rotation = Quaternion.Euler(48, -32, 0);
            var rim = new GameObject("Rim Light").AddComponent<Light>();
            rim.type = LightType.Directional; rim.color = SampleArt.Hex("#7fa8ff"); rim.intensity = .45f; rim.shadows = LightShadows.None;
            rim.transform.rotation = Quaternion.Euler(25, 150, 0);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = SampleArt.Hex("#4c5a7c");
            RenderSettings.ambientEquatorColor = SampleArt.Hex("#2b3247");
            RenderSettings.ambientGroundColor = SampleArt.Hex("#14171f");
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = SampleArt.Hex("#0e121a"); RenderSettings.fogDensity = .045f;
            RenderSettings.skybox = null;
        }

        private static void Arena(SampleArt art, CharacterBuilder.Palette m, Transform parent)
        {
            var darkStone = art.Opaque("Arena_DarkStone", "#2c313b", 0, .2f);
            var floorStone = art.Opaque("Arena_Floor", "#474d59", 0, .32f);
            var iron = art.Opaque("Arena_Iron", "#2d3138", .5f, .45f);
            var ember = art.Additive("Vfx_Ember", AssetDatabase.LoadAssetAtPath<Texture2D>(art.Folder + "/Vfx_Glow.asset"), "#ff8a3a");

            SampleArt.Part(parent, "Ground", art.Save(MeshKit.Lathe("Arena.Ground", new[] { new Vector2(40, -.02f), new Vector2(0, -.02f) }, 48, 1, 1, false, false)), darkStone, Vector3.zero);
            SampleArt.Part(parent, "Platform", art.Save(MeshKit.Lathe("Arena.Platform",
                new[] { new Vector2(7.2f, -.02f), new Vector2(7.2f, .02f), new Vector2(7.2f, .02f), new Vector2(7.05f, .06f), new Vector2(7.05f, .06f), new Vector2(0, .06f) }, 72, 1, 1, false, false)), floorStone, new Vector3(0, -.06f, 0));
            SampleArt.Part(parent, "InnerRing", art.Save(MeshKit.Torus("Arena.InnerRing", 3.2f, .035f, 96, 6)), m.Gold, new Vector3(0, .0f, 0), Vector3.zero, new Vector3(1, .4f, 1));
            SampleArt.Part(parent, "OuterRing", art.Save(MeshKit.Torus("Arena.OuterRing", 6.4f, .045f, 128, 6)), m.Gold, new Vector3(0, .0f, 0), Vector3.zero, new Vector3(1, .4f, 1));
            SampleArt.Part(parent, "CenterDisc", art.Save(MeshKit.Lathe("Arena.CenterDisc", new[] { new Vector2(3.12f, .002f), new Vector2(0, .002f) }, 72, 1, 1, false, false)), darkStone, Vector3.zero);

            var pillar = art.Save(MeshKit.Lathe("Arena.Pillar", new[]
            {
                new Vector2(.42f, 0), new Vector2(.42f, .18f), new Vector2(.42f, .18f), new Vector2(.32f, .26f), new Vector2(.28f, .34f),
                new Vector2(.25f, 2.9f), new Vector2(.32f, 3.0f), new Vector2(.4f, 3.12f), new Vector2(.4f, 3.12f), new Vector2(.4f, 3.24f), new Vector2(0, 3.24f),
            }, 20));
            var banner = art.Save(MeshKit.Cloth("Arena.Banner", .7f, 1.7f, .04f, 0, 8, 14, 2, .02f));
            var bannerTrim = art.Save(MeshKit.Cloth("Arena.BannerTrim", .7f, 1.7f, .04f, 0, 8, 2, 2, .02f, .93f, 1f, .004f));
            var bannerRod = art.Save(MeshKit.Sweep("Arena.BannerRod", new[] { new Vector3(-.45f, 0, 0), new Vector3(.45f, 0, 0) }, new[] { .025f, .025f }, 8));
            for (int i = 0; i < 8; i++)
            {
                float angle = (i + .5f) * Mathf.PI / 4;
                var position = new Vector3(Mathf.Sin(angle) * 6.9f, 0, Mathf.Cos(angle) * 6.9f);
                var column = SampleArt.Part(parent, "Pillar" + i, pillar, m.Stone, position);
                if (i % 2 != 0) continue;
                // 旗帜挂在立柱朝向场地中心的一面。
                var holder = new GameObject("Banner").transform;
                holder.SetParent(column.transform, false);
                holder.localPosition = new Vector3(0, 2.75f, 0);
                holder.rotation = Quaternion.LookRotation(-position.normalized);
                holder.position -= position.normalized * .32f;
                SampleArt.Part(holder, "Rod", bannerRod, m.Gold, Vector3.zero);
                SampleArt.Part(holder, "Cloth", banner, m.Navy, Vector3.zero, new Vector3(0, 180, 0));
                SampleArt.Part(holder, "Trim", bannerTrim, m.Gold, Vector3.zero, new Vector3(0, 180, 0));
                SampleArt.Part(holder, "Emblem", FindMesh(art, "Knight.Emblem"), m.Gold, new Vector3(0, -.65f, .075f), new Vector3(0, 0, 45), Vector3.one * 2.4f);
            }

            var bowl = art.Save(MeshKit.Lathe("Arena.Brazier", new[]
            {
                new Vector2(.28f, 0), new Vector2(.3f, .05f), new Vector2(.12f, .12f), new Vector2(.08f, .7f), new Vector2(.14f, .78f),
                new Vector2(.34f, .88f), new Vector2(.36f, .96f), new Vector2(.36f, .96f), new Vector2(.3f, .96f), new Vector2(.2f, .86f), new Vector2(0, .84f),
            }, 18));
            var flame = FindMesh(art, "Vfx.GlowCross");
            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI / 2 + Mathf.PI / 4;
                var position = new Vector3(Mathf.Sin(angle) * 4.7f, 0, Mathf.Cos(angle) * 4.7f);
                var brazier = SampleArt.Part(parent, "Brazier" + i, bowl, iron, position);
                var glow = SampleArt.Part(brazier.transform, "Flame", flame, ember, new Vector3(0, 1.05f, 0), Vector3.zero, new Vector3(.7f, 1f, .7f)).GetComponent<MeshRenderer>();
                glow.shadowCastingMode = ShadowCastingMode.Off;
                var light = new GameObject("FireLight").AddComponent<Light>();
                light.transform.SetParent(brazier.transform, false); light.transform.localPosition = new Vector3(0, 1.25f, 0);
                light.type = LightType.Point; light.color = SampleArt.Hex("#ff9a4a"); light.range = 5f; light.intensity = 1.6f; light.shadows = LightShadows.None;
            }
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        private static Mesh FindMesh(SampleArt art, string name)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(art.Folder + "/Meshes.asset"))
                if (asset is Mesh mesh && mesh.name == name) return mesh;
            return null;
        }
    }
}
