using System.Collections.Generic;
using System.Linq;
using GameplayTags.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFramework.GAS.Editor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>
    /// BasicCombat 示例生成器：程序化生成骑士与训练假人、六段动作、技能特效、五个技能与可游玩的训练场。
    /// 不依赖任何第三方美术资源。
    /// </summary>
    public static class BasicCombatSample
    {
        public const string Folder = "Assets/GAS Samples/BasicCombat";

        [MenuItem("Tools/ZFramework GAS/创建 BasicCombat 示例")]
        private static void CreateFromMenu()
        {
            if (AssetDatabase.IsValidFolder(Folder) &&
                !EditorUtility.DisplayDialog("重新生成 BasicCombat 示例",
                    $"将删除并重新生成 {Folder} 下的全部示例资产。\n同时会把示例用到的标签加入项目的 Gameplay Tags 配置并重新生成注册表。", "重新生成", "取消"))
                return;
            var ability = Create(true);
            SkillEditorWindow.Open().Show(ability);
            EditorGUIUtility.PingObject(ability);
        }

        /// <summary>重新生成整个示例目录（覆盖已有内容），返回「斩击」技能。</summary>
        /// <param name="registerTags">把示例标签写入项目标签配置并重新生成注册表。</param>
        public static GameplayAbilityAsset Create(bool registerTags = false)
        {
            const string folder = Folder;
            if (registerTags) RegisterTags();
            // 已打开的示例场景也要替换为本次生成结果，避免场景继续持有被删资产。
            string scenePath = folder + "/BasicCombat.unity";
            var loadedScene = SceneManager.GetSceneByPath(scenePath);
            bool reopenScene = loadedScene.IsValid() && loadedScene.isLoaded;
            bool wasActive = reopenScene && loadedScene == SceneManager.GetActiveScene();
            Scene replacementScene = default;
            if (reopenScene)
            {
                if (SceneManager.sceneCount == 1) replacementScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                EditorSceneManager.CloseScene(loadedScene, true);
            }
            foreach (var window in Resources.FindObjectsOfTypeAll<SkillEditorWindow>())
                if (window.Document?.Root != null && AssetDatabase.GetAssetPath(window.Document.Root).StartsWith(folder + "/", System.StringComparison.Ordinal)) window.Close();
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            SampleArt.EnsureFolder(folder + "/Prefabs");
            SampleArt.EnsureFolder(folder + "/Animations/Knight");
            SampleArt.EnsureFolder(folder + "/Animations/TrainingDummy");
            var art = new SampleArt(folder + "/Art");
            var palette = CharacterBuilder.CreatePalette(art);
            CreateAttributeCatalog(folder);

            var knight = CharacterBuilder.BuildKnight(art, palette);
            var rig = new PoseRig(knight);
            var clips = KnightAnimations.Bake(rig);
            // 文件名即片段名：Unity 导入时会把对象名改成文件名。
            foreach (var pair in clips) AssetDatabase.CreateAsset(pair.Value, folder + "/Animations/Knight/" + pair.Key + ".anim");
            var knightController = BuildController(folder + "/Animations/Knight/Knight.controller", clips["Idle"],
                new[] { "Slash", "Bolt", "Warcry", "Sanctuary", "Summon" }, clips);
            knight.AddComponent<Animator>().runtimeAnimatorController = knightController;
            knight.AddComponent<SkillAnimator>();
            var cueLibrary = SampleVfx.Build(art, knight, clips["Slash"], folder);
            knight.AddComponent<CueVfxPlayer>().SetLibrary(cueLibrary);
            rig.ResetToBind();
            var knightPrefab = SavePrefab(knight, folder + "/Prefabs/Knight.prefab");

            var dummy = CharacterBuilder.BuildDummy(art, palette);
            var hit = KnightAnimations.DummyHit();
            AssetDatabase.CreateAsset(hit, folder + "/Animations/TrainingDummy/Hit.anim");
            dummy.AddComponent<Animator>().runtimeAnimatorController = BuildController(folder + "/Animations/TrainingDummy/TrainingDummy.controller", null,
                new[] { "Hit" }, new Dictionary<string, AnimationClip> { ["Hit"] = hit });
            var dummyPrefab = SavePrefab(dummy, folder + "/Prefabs/TrainingDummy.prefab");

            var bolt = SavePrefab(SampleVfx.BuildProjectile(art), folder + "/Prefabs/ArcaneBolt.prefab");
            var skills = SampleSkills.Create(folder + "/Skills", bolt);
            AssetDatabase.SaveAssets();
            // 建场景可能卸载未被引用的资产，写布局又会重新导入技能：此后一律按路径重新加载。
            var paths = skills.Select(AssetDatabase.GetAssetPath).ToArray();
            string knightPath = AssetDatabase.GetAssetPath(knightPrefab);
            SceneBuilder.Build(scenePath, art, palette, knightPrefab, dummyPrefab, skills, replacementScene);
            knightPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(knightPath);
            // 预览角色记在技能的编辑器布局里，打开技能即可看到骑士。
            foreach (var path in paths)
                new LayoutStore(AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(path)) { PreviewPrefab = knightPrefab }.Save();
            AssetDatabase.SaveAssets();
            SkillIndex.Invalidate();
            if (reopenScene && !SceneManager.GetSceneByPath(scenePath).isLoaded)
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            if (wasActive) SceneManager.SetActiveScene(SceneManager.GetSceneByPath(scenePath));
            return AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(paths[0]);
        }

        /// <summary>把示例标签加入项目标签配置；有新增时重新生成注册表（随后会触发一次脚本编译）。</summary>
        public static void RegisterTags()
        {
            var settings = GameplayTagSettings.GetOrCreate();
            var existing = new HashSet<string>(settings.Entries.Select(entry => entry.Name));
            var missing = SampleTags.All.Where(tag => !existing.Contains(tag)).ToArray();
            if (missing.Length == 0) return;
            foreach (var tag in missing) settings.SaveTag(null, tag, "BasicCombat 示例");
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            GameplayTagRegistryGenerator.Generate();
            Debug.Log("BasicCombat：已向标签配置添加 " + missing.Length + " 个标签：" + string.Join("，", missing), settings);
        }

        private static void CreateAttributeCatalog(string folder)
        {
            var catalog = ScriptableObject.CreateInstance<AttributeCatalogAsset>();
            catalog.entries = new[]
            {
                new AttributeCatalogEntry { id = SampleSkills.Health, displayName = "生命", category = "示例" },
                new AttributeCatalogEntry { id = SampleSkills.MaxHealth, displayName = "最大生命", category = "示例" },
                new AttributeCatalogEntry { id = SampleSkills.Attack, displayName = "攻击", category = "示例" },
                new AttributeCatalogEntry { id = SampleSkills.Stamina, displayName = "体力", category = "示例" },
            };
            AssetDatabase.CreateAsset(catalog, folder + "/Attributes.asset");
            AttributeCatalog.Invalidate();
        }

        /// <summary>
        /// 待机为默认状态；每个技能状态播完后自动回到待机。
        /// 技能由 <see cref="AnimatorAnimationDriver"/> 按「Base Layer.状态名」直接播放。
        /// </summary>
        private static AnimatorController BuildController(string path, AnimationClip idle, IEnumerable<string> actions, IReadOnlyDictionary<string, AnimationClip> clips)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var machine = controller.layers[0].stateMachine;
            var idleState = machine.AddState("Idle", new Vector3(260, 0));
            idleState.motion = idle;
            machine.defaultState = idleState;
            int row = 0;
            foreach (var name in actions)
            {
                var state = machine.AddState(name, new Vector3(560, -120 + row++ * 70));
                state.motion = clips[name];
                var back = state.AddTransition(idleState);
                back.hasExitTime = true; back.exitTime = .94f; back.duration = .12f; back.hasFixedDuration = true;
            }
            return controller;
        }

        private static GameObject SavePrefab(GameObject instance, string path)
        {
            try { return PrefabUtility.SaveAsPrefabAsset(instance, path); }
            finally { Object.DestroyImmediate(instance); }
        }
    }
}
