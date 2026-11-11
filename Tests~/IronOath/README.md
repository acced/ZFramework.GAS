# Iron Oath portable gameplay regressions

Run from the repository root with .NET 8 and the pinned sibling GameplayTags checkout used by the existing Core tests:

```bash
dotnet build Tests~/IronOath/IronOath.Tests.csproj -c Release -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet .build~/iron-oath/bin/Release/net8.0/IronOath.Tests.dll
```

The runner uses the **real GAS Core and GameplayTags assemblies**. The game domain has no Unity dependencies. Tests exercise complete effects, costs, battle steps, formation/tactics, status composition and loot, rather than a second copy of the formulas.

Only a warmed, explicitly measured simulation path may be described as allocating 0 B. Game creation, UI updates, strings, loading, save serialization and Unity/Spine rendering are outside that claim.

Unity scene import, UGUI, font rendering, input handling, optional Spine prefabs and IL2CPP require a real Unity 6000.3 project. This console suite does not claim to validate them.
