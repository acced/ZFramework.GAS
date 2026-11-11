# Iron Oath · real Unity acceptance

These suites use Unity's imported assets, `JsonUtility`, Canvas layout, EventSystem
raycasts and PlayMode object lifecycle. They complement the portable rules tests;
neither suite replaces the other. Unity Editor is required. The development container
does not include Unity, so adding these files does **not** mean these tests have passed.

## Prepare an isolated project

Use Python 3.10 or later and an installed, licensed Unity **6000.3** editor. From the
package checkout:

```bash
python3 Tools~/prepare_iron_oath_unity.py ../IronOathAcceptance --unity-version 6000.3.0f1
```

Replace the version with your installed 6000.3 patch. An optional
`--gameplay-tags /path/to/ZFramework.GameplayTags` uses a clean local dependency at
`8dde1a43485f33031fd73b4e6cb21304f03c19df`; otherwise Package Manager fetches that exact
Git commit. The destination must be new or empty and outside the package checkout.
Existing projects are never overwritten. Sample `.meta` GUIDs are preserved and
Spine remains optional. Open the generated `ACCEPTANCE.md` for exact batch commands.

The package source is linked, while sample and test source is copied. Recreate the
acceptance project after changing those copied files. The script does not install or
launch Unity, alter an existing project, or produce an engine verification result.

## Run in Unity

1. Open the generated project and wait for package resolution and compilation.
2. Run **Tools > Iron Oath > Validate Imported Demo**. The checker opens the saved
   demo in a preview scene and always closes it; it checks resources, font glyphs,
   shader compiler errors, missing scripts, entry points and world actor bindings.
   **Validate Current Scene** also checks unsaved actor settings and camera-layer
   collisions in the currently open scene without changing it.
3. In **Window > General > Test Runner**, run both assemblies:

   - `IronOath.Unity.EditMode.Tests`: 11 test cases. Import checks, mirrored board
     bounds at three aspect ratios, actual `JsonUtility` campaign/preferences
     roundtrip, absent reference flags, pending reward tickets, backup recovery,
     and malformed profile rejection.
   - `IronOath.Unity.PlayMode.Tests`: 5 test cases. World actors and camera isolation,
     first-frame HUD projection and changed camera aspects, inactive prefab activation
     with authored transforms/sorting, nested sorting groups, event delivery, immediate
     teardown, real pointer-click UI journey through practice/battle/rewards/reload,
     and full-bag discard confirmation followed by a single payout.

Every save test uses a fresh temporary directory. PlayMode tests set the sample's
internal save directory override before `Start`; no normal player saves are read,
deleted, or overwritten. The UI journey uses the actual Canvas raycaster and button
pointer handler rather than invoking private gameplay methods.

The default bootstrap covers the built-in render pipeline and legacy input. Repeat
PlayMode after installing Input System and choosing **Input System Package (New)**
to exercise its UI module. Optional Spine needs your own licensed runtime and prefab
assets and is not enabled by the bootstrap. Repeat scene validation after binding
your actors.

## Visual and device checks still required

Open the demo and inspect the real Game view at 16:9, 4:3 and ultrawide resolutions.
The automated aspect test changes the camera aspect; it does not resize an operating
system window or prove device safe-area handling. Check the background, feet, HUD,
front/back overlap, mixed assigned/default actors, defeat poses and complete Chinese
labels. Inspect the Console for errors and retain screenshots with the tested commit.
Run the intended URP setup, Spine actions, IL2CPP build and mobile profiler separately.
Rendering quality and target-device performance cannot be established by .NET tests.

The Python scaffold guards can run without Unity:

```bash
python3 -m unittest discover -s Tests~/IronOathBootstrap -p 'test_*.py'
```

They verify directory protection, pinned dependency validation, paths containing
spaces, sample GUID preservation and exclusion of the optional Spine sources.
