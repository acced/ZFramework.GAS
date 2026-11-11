#!/usr/bin/env python3
"""Prepare a NEW, isolated Unity 6000.3 project for real Iron Oath acceptance.

This script does not install Unity, run a substitute engine, or rewrite an existing
project. The GAS package stays linked to this checkout, while the optional sample
and Unity tests are copied with their metadata. Recreate the project after sample
changes so the copied source matches the checkout under review.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


ROOT = Path(__file__).resolve().parents[1]
TAGS_COMMIT = "8dde1a43485f33031fd73b4e6cb21304f03c19df"
TAGS_PACKAGE = "com.acced.zframework.gameplaytags"


def package_reference(package: Path, project: Path) -> str:
    try:
        path = Path(os.path.relpath(package, project / "Packages")).as_posix()
    except ValueError:  # Windows: different drive letters.
        path = package.as_posix()
    return "file:" + path


def write_json(path: Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def prepare(project: Path, unity_version: str, tags: Path | None):
    project = project.resolve()
    if project == ROOT or ROOT in project.parents:
        raise ValueError("Choose a separate directory outside the GAS package checkout.")
    if project.exists() and (not project.is_dir() or any(project.iterdir())):
        raise ValueError("Destination must be new or completely empty; no existing project will be overwritten.")
    if not re.fullmatch(r"6000\.3\.\d+[abfp]\d+", unity_version):
        raise ValueError("Use the exact installed Unity 6000.3 editor version, for example 6000.3.0f1.")
    package = json.loads((ROOT / "package.json").read_text(encoding="utf-8"))
    if package.get("name") != "com.acced.zframework.gas":
        raise ValueError("Run this script from the ZFramework.GAS checkout.")
    if tags is None:
        tags_ref = "https://github.com/acced/ZFramework.GameplayTags.git#" + TAGS_COMMIT
    else:
        tags = tags.resolve()
        tags_package = json.loads((tags / "package.json").read_text(encoding="utf-8"))
        if tags_package.get("name") != TAGS_PACKAGE:
            raise ValueError("--gameplay-tags is not the GameplayTags package checkout.")
        revision = subprocess.run(["git", "-C", str(tags), "rev-parse", "HEAD"],
                                  check=True, capture_output=True, text=True).stdout.strip()
        dirty = subprocess.run(["git", "-C", str(tags), "status", "--porcelain", "--untracked-files=normal"],
                               check=True, capture_output=True, text=True).stdout.strip()
        if revision != TAGS_COMMIT or dirty:
            raise ValueError("--gameplay-tags must be clean at pinned commit " + TAGS_COMMIT + ".")
        tags_ref = package_reference(tags, project)
    sample = ROOT / "Samples~" / "IronOath"
    tests = ROOT / "Tests~" / "IronOath.Unity"
    if not sample.is_dir() or not tests.is_dir():
        raise ValueError("Sample or Unity acceptance sources are missing from this checkout.")

    # All preflight checks precede writes. copytree never replaces an existing tree.
    (project / "Assets").mkdir(parents=True, exist_ok=True)
    shutil.copytree(sample, project / "Assets" / "IronOath",
                    ignore=shutil.ignore_patterns("OptionalSpine~", "OptionalSpine~.meta"))
    if sample.with_suffix(".meta").exists():
        shutil.copy2(sample.with_suffix(".meta"), project / "Assets" / "IronOath.meta")
    shutil.copytree(tests, project / "Assets" / "IronOathTests")
    tests_meta = Path(str(tests) + ".meta")
    if tests_meta.exists():
        shutil.copy2(tests_meta, project / "Assets" / "IronOathTests.meta")
    dependencies = {
        "com.acced.zframework.gas": package_reference(ROOT, project),
        TAGS_PACKAGE: tags_ref,
        "com.unity.ugui": "2.0.0",
        "com.unity.test-framework": "1.6.0",
        "com.unity.modules.jsonserialize": "1.0.0",
        "com.unity.modules.imageconversion": "1.0.0",
        "com.unity.modules.ui": "1.0.0",
        "com.unity.modules.uielements": "1.0.0",
        "com.unity.modules.imgui": "1.0.0",
    }
    write_json(project / "Packages" / "manifest.json", {"dependencies": dependencies})
    (project / "ProjectSettings").mkdir()
    (project / "ProjectSettings" / "ProjectVersion.txt").write_text(
        "m_EditorVersion: " + unity_version + "\n", encoding="utf-8")
    (project / "TestResults").mkdir()
    (project / ".gitignore").write_text(
        "Library/\nTemp/\nLogs/\nUserSettings/\nTestResults/\nObj/\n*.csproj\n*.sln\n", encoding="utf-8")
    instructions = """# Iron Oath Unity acceptance project

This is a disposable validation project, not a second source of truth. GAS is linked
to the original checkout; the sample and tests are copied. Recreate this directory
after changing sample/test source. Unity must resolve its packages and compile first.

1. Open this directory with the specified Unity 6000.3 editor.
2. Run **Tools > Iron Oath > Validate Imported Demo**.
3. Open **Window > General > Test Runner** and run IronOath EditMode and PlayMode tests.
4. Open **Tools > Iron Oath > Open Demo**, press Play, and inspect the actual Game view.
   Check 16:9, 4:3 and ultrawide layouts; HUD alignment; both facing directions; mixed
   placeholder/prefab row occlusion; and the supplied Chinese font. Automated tests
   do not establish final art quality, Spine asset compatibility, or mobile performance.

Command line (replace UNITY with the installed editor executable):

```text
UNITY -batchmode -quit -projectPath "PROJECT" -executeMethod IronOath.Editor.IronOathValidation.ValidateBatch -logFile "PROJECT/TestResults/import.log"
UNITY -batchmode -projectPath "PROJECT" -runTests -testPlatform EditMode -testFilter IronOath.UnityTests -testResults "PROJECT/TestResults/editmode.xml" -logFile "PROJECT/TestResults/editmode.log"
UNITY -batchmode -projectPath "PROJECT" -runTests -testPlatform PlayMode -testFilter IronOath.UnityTests -testResults "PROJECT/TestResults/playmode.xml" -logFile "PROJECT/TestResults/playmode.log"
```

Run the commands sequentially. Do not add `-quit` to the test commands: the Test
Framework exits after producing results. Use a graphics-enabled session (or virtual
display) for PlayMode; `-nographics` cannot verify renderers or screenshots. No Unity
license or editor is downloaded by this script.

The bootstrap uses the built-in pipeline and legacy input as its baseline. To also
verify the new Input System, install that package in this disposable project, set
Active Input Handling to Input System Package (New), allow the editor to restart,
and rerun PlayMode. The input smoke check verifies the corresponding active UI module.
The optional Spine folders are excluded; bind your licensed Spine prefabs separately.
Each test uses a unique temporary save directory and never writes the normal save.
""".replace("PROJECT", project.as_posix())
    (project / "ACCEPTANCE.md").write_text(instructions, encoding="utf-8")
    return project


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="new or empty directory outside the package checkout")
    parser.add_argument("--unity-version", required=True, help="exact installed 6000.3 editor version")
    parser.add_argument("--gameplay-tags", type=Path, help="optional clean local checkout at the pinned GameplayTags commit")
    args = parser.parse_args()
    try:
        result = prepare(args.destination, args.unity_version, args.gameplay_tags)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, "Cannot prepare acceptance project: " + str(error) + "\n")
    print("Prepared Unity acceptance project: " + str(result))
    print("Open ACCEPTANCE.md for import, EditMode, PlayMode and visual checks. Unity has not been run.")


if __name__ == "__main__":
    main()
