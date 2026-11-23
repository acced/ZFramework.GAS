# Iron Oath portable gameplay regressions

Run from the repository root with .NET 8 and the pinned sibling GameplayTags checkout used by the existing Core tests:

```bash
dotnet build Tests~/IronOath/IronOath.Tests.csproj -c Release -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet .build~/iron-oath/bin/Release/net8.0/IronOath.Tests.dll
```

The runner uses the **real GAS Core and GameplayTags assemblies**. The game domain has no Unity dependencies. Tests exercise complete effects, costs, battle steps, formation/tactics, status composition and loot, rather than a second copy of the formulas.

Current baseline: **47 passing groups** — 19 combat, 17 campaign, 5 integration, 6 persistence. The integration suite runs real battles through all 15 campaign nodes for both protagonists, settles the shared reward bridge, sells stock, and pays Sunday rent. It also exercises saved active tickets, full-bag pending results, exact armor condition mapping, nonterminal result rejection, and defeat/draw without loot.

Pending reward cases cover a real six-humanoid battle producing 36 cells of preserved armor, a smaller batch whose geometry does not fit, and discarding every item while keeping coins, tickets and clue ownership correct. Explicit claiming remains atomic and cannot pay twice.

Persistence tests link the actual engine-independent save store and use real temporary files with an injected System.Text.Json codec. They verify complete formation/tactics validation, v1 field compatibility, backup rotation and recovery, unreadable or oversized primary files, rejected writes, and active/pending expedition checkpoints. They do **not** simulate or validate Unity's JsonUtility; its real serialization tests live in [IronOath.Unity](../IronOath.Unity/README.md).

Only a warmed, explicitly measured simulation path may be described as allocating 0 B. Game creation, UI updates, strings, loading, save serialization and Unity/Spine rendering are outside that claim.

Unity scene import, UGUI, font rendering, input handling, optional Spine prefabs and IL2CPP require a real Unity 6000.3 project. This console suite does not claim to validate them.
