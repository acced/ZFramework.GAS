// Reproduce with Unity 6000.3: copy into Testproject/Assets/Editor, then invoke
// -batchmode -projectPath <Testproject> -executeMethod GASBlendFrameCapture.Run -quit
// Uses the project's generated assets without saving changes to them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameplayTags;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS;
using ZFramework.GAS.Editor;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Samples.BasicCombat;
using Object = UnityEngine.Object;

public static class GASBlendFrameCapture
{
    const string Folder = "Assets/GAS Samples/BasicCombat";
    const string Output = "D:/UGit/ZFramework.GAS/.build~/blend-capture/raw";
    const int Width = 560, Height = 600, Fps = 60;

    // Run only in the disposable validation project; never regenerates the user's open scene.
    public static void GenerateAndRun()
    {
        string expected = Path.GetFullPath("C:/Users/pc/Testproject/GASRefactorValidation~/Assets");
        if (!string.Equals(Path.GetFullPath(Application.dataPath), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GenerateAndRun requires the isolated validation project");
        if (AssetDatabase.IsValidFolder("Assets/GAS Samples/CueVfxDemo"))
            AssetDatabase.DeleteAsset("Assets/GAS Samples/CueVfxDemo");
        ZFramework.GAS.Samples.BasicCombat.Editor.BasicCombatSample.Create(true);
        Run();
    }
    [Serializable] public class Report
    {
        public string unity, project, capturedUtc, method;
        public int width = Width, height = Height, fps = Fps;
        public List<Sequence> sequences = new();
    }
    [Serializable] public class Sequence
    {
        public string id, label, skillPath, clip;
        public float blendIn, blendOut, duration, cancelAt;
        public bool preview;
        public List<Frame> off = new(), on = new();
    }
    [Serializable] public class Frame
    {
        public int frame;
        public float time;
        public string image, state, nextState;
        public bool active, transition;
        public float transitionProgress;
        public Vector3 swordTip, rightHand, head;
    }

    public static void Run()
    {
        Directory.CreateDirectory(Output);
        try
        {
            GameplayTagManager.Initialize(SampleTags.All.Select(t => new GameplayTagRegistration(t)).ToArray());
            var report = new Report { unity = Application.unityVersion, project = Application.dataPath,
                capturedUtc = DateTime.UtcNow.ToString("o"),
                method = "SkillRunner.TryCast -> AnimationTask -> AnimatorAnimationDriver; Animator.Update and ASC.Tick manually evaluated at 60 Hz in isolated Unity preview scenes; Camera rendering. No clip curve editing; VFX excluded. Asset copies only." };
            Capture(report, "slash", "斩击 · 完整播放", "斩击", 1.30f);
            Capture(report, "warcry", "战吼 · 完整播放", "战吼", 1.60f);
            Capture(report, "cancel", "战吼 · 0.65 秒打断", "战吼", 1.25f, .65f);
            Capture(report, "preview", "技能编辑器的 Clip 采样", "战吼", .20f, -1, true);
            File.WriteAllText(Path.Combine(Output, "capture.json"), JsonUtility.ToJson(report, true));
            File.WriteAllText(Path.Combine(Output, "PASS.txt"), "Captured " + report.sequences.Sum(s => s.on.Count + s.off.Count) + " actual Unity frames.\n" + report.method);
            Debug.Log("GAS_BLEND_CAPTURE_PASS " + Output);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(Output, "FAIL.txt"), e.ToString());
            Debug.LogException(e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static void Capture(Report report, string id, string label, string skillName, float end, float cancelAt = -1, bool preview = false)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(Folder + "/Skills/" + skillName + ".asset");
        if (source == null || source.Animation == null) throw new Exception("Missing generated skill " + skillName);
        var sequence = new Sequence { id = id, label = label, skillPath = AssetDatabase.GetAssetPath(source),
            clip = source.Animation.ClipName, blendIn = source.Animation.BlendIn, blendOut = source.Animation.BlendOut,
            duration = source.Animation.Duration, cancelAt = cancelAt, preview = preview };
        CaptureVariant(sequence, source, false, end);
        CaptureVariant(sequence, source, true, end);
        report.sequences.Add(sequence);
    }

    static AbilitySystemComponent NewAsc()
    {
        var asc = new AbilitySystemComponent();
        asc.Attributes.Add(new AttributeId(1), 300);
        asc.Attributes.Add(new AttributeId(2), 300);
        asc.Attributes.Add(new AttributeId(3), 20);
        asc.Attributes.Add(new AttributeId(4), 100);
        return asc;
    }

    static void CaptureVariant(Sequence sequence, GameplayAbilityAsset source, bool blend, float end)
    {
        var asset = Object.Instantiate(source);
        var animation = Object.Instantiate(source.Animation);
        using (var so = new SerializedObject(asset))
        {
            so.FindProperty("animation").objectReferenceValue = animation;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        using (var so = new SerializedObject(animation))
        {
            so.FindProperty("blendIn").floatValue = blend ? sequence.blendIn : 0;
            so.FindProperty("blendOut").floatValue = blend ? sequence.blendOut : 0;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        asset.InvalidateDefinition(); animation.InvalidateDefinition();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Prefabs/Knight.prefab");
        var render = new PreviewRenderUtility();
        IPoseSampler sampler = null;
        SkillRunner runner = null;
        try
        {
            PreviewStage.Configure(render);
            var knight = render.InstantiatePrefabInScene(prefab);
            foreach (var component in knight.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null) component.enabled = false;
            var animator = knight.GetComponent<Animator>();
            animator.enabled = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Play("Base Layer.Idle", 0, 0f);
            animator.Update(0);
            animator.Update(.6f);
            var all = knight.GetComponentsInChildren<Transform>();
            Transform Find(string name) => all.Single(t => t.name == name);
            var tip = Find("SwordTip");
            var hand = Find("Hand.R");
            var head = Find("Head");
            PreviewStage.AddGround(render, new Bounds(new Vector3(0, 1, 0), new Vector3(1.5f, 2, 1.5f)));
            render.camera.orthographic = true;
            render.camera.orthographicSize = 1.80f;
            render.camera.nearClipPlane = .01f;
            render.camera.farClipPlane = 50;
            render.camera.transform.position = new Vector3(4.2f, 2.7f, 6.5f);
            render.camera.transform.LookAt(new Vector3(0, 1.45f, 0));
            var owner = NewAsc();
            if (sequence.preview) sampler = AnimationAdapterRegistry.Resolve(prefab).BeginPreview(knight);
            else
            {
                runner = new SkillRunner(owner, () => knight.GetComponent<SkillAnimator>().Driver);
                var result = runner.TryCast(asset, NewAsc());
                if (result != AbilityActivationResult.Activated) throw new Exception("Capture cast failed: " + result);
            }
            string variant = blend ? "on" : "off";
            string directory = Path.Combine(Output, sequence.id, variant);
            Directory.CreateDirectory(directory);
            int count = Mathf.RoundToInt(end * Fps);
            float previous = 0;
            for (int index = 0; index <= count; index++)
            {
                float time = index / (float)Fps;
                if (sequence.preview) sampler.Sample(animation.ClipName, time);
                else if (index > 0)
                {
                    float dt = time - previous;
                    animator.Update(dt);
                    owner.Tick(dt);
                    if (sequence.cancelAt >= 0 && index == Mathf.RoundToInt(sequence.cancelAt * Fps))
                        if (!runner.Cancel(asset)) throw new Exception("Cancellation did not end the skill");
                    animator.Update(0);
                }
                previous = time;
                string filename = index.ToString("D3") + ".png";
                render.BeginStaticPreview(new Rect(0, 0, Width, Height));
                render.Render(true);
                var image = render.EndStaticPreview();
                try { File.WriteAllBytes(Path.Combine(directory, filename), image.EncodeToPNG()); }
                finally { Object.DestroyImmediate(image); }
                var record = new Frame { frame = index, time = time, image = sequence.id + "/" + variant + "/" + filename,
                    active = runner != null && runner.IsActive(asset), transition = animator.IsInTransition(0),
                    state = StateName(animator.GetCurrentAnimatorStateInfo(0)), nextState = StateName(animator.GetNextAnimatorStateInfo(0)),
                    transitionProgress = animator.IsInTransition(0) ? animator.GetAnimatorTransitionInfo(0).normalizedTime : 0,
                    swordTip = tip.position, rightHand = hand.position, head = head.position };
                (blend ? sequence.on : sequence.off).Add(record);
            }
        }
        finally
        {
            runner?.Cancel(asset);
            sampler?.Dispose();
            render.Cleanup();
            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(animation);
        }
    }

    static string StateName(AnimatorStateInfo state)
    {
        foreach (var name in new[] { "Idle", "Slash", "Bolt", "Warcry", "Sanctuary", "Summon" })
            if (state.IsName("Base Layer." + name)) return name;
        return "";
    }
}
