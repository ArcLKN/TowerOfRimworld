# TowerOfRimworld

## Building and deploying

The mod's assembly is loaded by the game from `1.6/Mods/Biotech/Assemblies/`, and the in-game
Publish button uploads this folder as-is — so **build output must never be produced inside it**.

```bash
dotnet build -c Release     # builds and copies the DLL into the shipped location
```

- Output goes to `X:\AI\TowerOfRimworld-build\bin\<Configuration>\net4.7.2\` — outside the mod
  folder, outside the game tree, and therefore impossible to publish by accident. Override with
  `-p:ToRBuildRoot=D:\somewhere\` on another machine.
- A **Release** build deploys automatically and prints the shipped file's SHA-256. Run it
  explicitly with `dotnet msbuild Tower_of_Rimworld.csproj -t:Deploy -p:Configuration=Release`.
- Testing a **Debug** build in game: `dotnet msbuild Tower_of_Rimworld.csproj -t:DeployDev
  -p:Configuration=Debug` — it copies the Debug DLL into the shipped slot and warns you to rebuild
  Release before publishing. `Deploy` refuses to run on a Debug build.
- Only the `.dll` is deployed: no `.pdb` ever reaches the shipped folder.
- `1.5/Mods/Biotech/Assemblies/` is **never** written by the deploy: 1.5 is frozen, and that binary
  was built against 1.5's assemblies, so it must stay byte-identical.
- Close the game before a Release build: RimWorld locks the shipped DLL, so the deploy would fail
  the build (which is deliberate — a green build means the shipped file really was updated).
