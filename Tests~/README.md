# Core regression

Run from the package directory with .NET 8 installed:

```powershell
dotnet run --project Tests~/Core/Core.csproj -c Release
```

The executable initializes the full test vocabulary once before running any case. It exits nonzero on failure. The Core projects target .NET Standard 2.1 and do not reference Unity.

Coverage: hierarchy boundaries; multiple effect/ability sources; contradictory activation and application requirements; cooldown; cancellation and effect removal by parent; ongoing conditions; hierarchical Cue dispatch; immutable configuration; explicit and ancestor events; reentrant Clear; throwing removal callbacks; name and DataContract round trips; unknown/empty names on load; blank required/granted asset rows; preservation of serialized input; 8,192 rule/state combinations; 20,000 mixed counted mutations with and without listeners.

The test project links the production `Runtime/Unity/TagAssetUtility.cs` directly. This loading helper has no engine dependency, so its actual filtering and copying behavior runs in .NET without Unity stubs or copying its implementation into tests. It remains in the Unity assembly in the shipped package.

The warmed activation/end loop also asserts zero thread-local managed allocation. Timings depend on the JIT, hardware, tag hierarchy and workload; zero allocation does not establish latency equivalence for every workload.
