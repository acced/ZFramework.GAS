# Verification

## Portable Core

Use .NET 8 and clone ZFramework.GameplayTags 1.0.0 beside this repository. The CI workflow pins its resolved commit, `8dde1a43485f33031fd73b4e6cb21304f03c19df`.

```bash
python3 Tools~/verify_architecture.py
dotnet build Tests~/Core/Core.csproj -c Release -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet .build~/tests/bin/Release/net8.0/Core.dll
```

The executable exits nonzero on a regression. It initializes the complete test tag vocabulary once. Production Core targets .NET Standard 2.1 without Unity references. Outputs stay in `.build~/` and are not Unity plugins.

Coverage includes hierarchy boundaries, counted tag ownership, effect stacking/removal/periods, immutable definitions, activation/commit/end, source recycling, reentrant callbacks, task scheduling, Cue dispatch, parameter evaluation and warmed allocation assertions. The linked production TagAssetUtility helper is engine-free; it is tested without a copied implementation or Unity stubs.

`Tools~/verify_architecture.py` checks local assembly boundaries, dependency cycles, physics isolation, imported asset metadata and optional samples. It is a static check, not a C# or Unity compiler.

## Unity 6000.3

`Tests~` is ignored by normal UPM import. Copy the desired test folders into a Unity project with the Unity Test Framework installed.

### Base editor configuration

Import GameplayTags, GAS Runtime excluding `Runtime/Unity/Realtime` and `Runtime/Unity/Combat3D`, and GAS Editor. Copy `Tests~/Editor.Base` into the project's Assets test folder. Do not import Editor.Realtime, Spine extensions or BasicCombat for this configuration.

Run EditMode tests. This configuration verifies creation, editing, strict build diagnostics, parameter scopes and deferred commit without a physics or animation dependency.

### Full package configuration

1. Install the complete GAS package and GameplayTags.
2. Import Basic Combat from the package's Samples panel.
3. Register the sample vocabulary using its generation workflow.
4. Copy `Tests~/Editor` and `Tests~/PlayMode` into Assets test folders. Their asmdefs explicitly reference Realtime, Combat3D and sample assemblies.
5. Generate BasicCombat assets when a test's setup requires them; then run EditMode and PlayMode suites.
6. Run the existing platform smoke workflow in `Tests~/Smoke` for the target Unity/IL2CPP environment.

`LibraryBoundaryTests` exercises a project-supplied fixed target without physics, rejects implicit spatial services, and queries an isolated PhysicsScene. CompilationTests and the editor regression exercise F7/Player-build validation semantics.

The repository's .NET CI does not execute Unity tests or validate mobile performance. Actual results and limitations of the 0.2 change are recorded in `Docs~/Refactor-0.2.md`.
