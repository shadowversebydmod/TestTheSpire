# TestTheSpire

[![NuGet](https://img.shields.io/nuget/v/TestTheSpire.svg)](https://www.nuget.org/packages/TestTheSpire/0.1.4)

TestTheSpire is a testing framework for Slay the Spire 2. It lets STS2 mod tests run from a plain command-line environment, without starting the game through Steam. In an AI-assisted development workflow, the same loop can write code, run tests, and bring the result back for review. Long card implementation runs become easier to keep reliable because failures point to a concrete combat test instead of a manual launch step.

This project depends on a local STS2 install. The build references `sts2.dll` and `GodotSharp.dll`, and the run targets start the STS2 executable with headless arguments.

中文说明见 [README.md](README.md).

## Projects Using TestTheSpire

- [Shadowverse Beyond Mod](https://www.nexusmods.com/slaythespire2/mods/977?tab=description)

## Use TestTheSpire In Your Own Project

**The currently verified environments include WSL2/Linux. Windows should be able to run it in theory, but it needs different configuration. We are working on broader support; please be patient.**

**You may need to know how to use SteamCMD to fetch Slay the Spire 2 in a Linux environment.**

Create a separate test project, for example `YourMod.Tests/YourMod.Tests.csproj`. The test project usually references the mod project and the TestTheSpire package:

```bash
dotnet add YourMod.Tests/YourMod.Tests.csproj package TestTheSpire --version 0.1.4
```

A minimal project file can look like this:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>yourmod_tests</AssemblyName>
    <RootNamespace>YourMod.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../yourmod.csproj" />
    <PackageReference Include="TestTheSpire" Version="0.1.4" />
  </ItemGroup>
</Project>
```

When testing an unpublished local TestTheSpire build, pack this repository first, then restore the test project from the local package folder:

```xml
<RestoreSources>../TestTheSpire/artifacts/packages;$(RestoreSources)</RestoreSources>
```

The test assembly needs an STS2 mod initializer. The initializer gives TestTheSpire the assembly that contains the xUnit facts:

```csharp
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using TestTheSpire;

namespace YourMod.Tests;

[ModInitializer("Init")]
public static class Entry
{
    public static void Init()
    {
        CombatTestBootstrap.Initialize(Assembly.GetExecutingAssembly(), new CombatTestOptions
        {
            LogPrefix = "yourmod.Tests"
        });

        Log.Info("[yourmod.Tests] Mod initialized");
    }
}
```

If the project keeps a static manifest, put `TestTheSpire` before the tested mod. The STS2 loader will load `xunit.v3.assert`, `TestTheSpire`, the tested mod, and then the test mod:

```json
{
  "id": "yourmod_tests",
  "name": "yourmod.Tests",
  "author": "your team",
  "description": "Headless combat tests for yourmod.",
  "version": "0.1.4",
  "has_pck": false,
  "has_dll": true,
  "dependencies": [
    { "id": "TestTheSpire", "min_version": "0.1.4" },
    { "id": "yourmod", "min_version": null }
  ],
  "affects_gameplay": true
}
```

The MSBuild targets can also generate the test manifest during `CopySts2TestPayload`. The generated manifest uses `$(Sts2TestModId)`, `$(Sts2MainModId)`, and the current assembly name.

## Configure STS2 Paths

Pass the STS2 root directory when running tests:

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:ListSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2
```

Standard Steam installs can usually be detected by a project-level `GameFolder.props`. Teams often hit path differences across developer machines: one person keeps STS2 on another disk, another uses an extracted game folder, and CI may mount only the game directory. Passing the properties in the command line makes the run explicit:

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:RunSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2
```

If a CI image mounts only the data directory, pass `Sts2DataDir` directly. If a local run needs a custom executable or mods directory, pass `Sts2Executable` and `Sts2ModsDir`.

## Run Tests

List tests without executing them:

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:ListSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2
```

When a developer is changing one card, a focused test keeps the run short. The failure output stops at a specific fact, and reviewers can see which combat path changed:

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:RunSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2 \
  -p:Sts2TestArgs=--sts2-test-filter=Strike_deals_six_damage
```

Useful targets:

- `CopySts2TestPayload`: builds the test assembly and writes the test mod payload.
- `InstallSts2TestMod`: copies `TestTheSpire`, `xunit.v3.assert`, and the test mod into the STS2 `mods` directory.
- `ListSts2Tests`: starts STS2 headless and prints discovered xUnit facts.
- `RunSts2Tests`: starts STS2 headless and executes matching tests.

`ListSts2Tests` and `RunSts2Tests` write the full STS2 stdout/stderr stream to `list.log` or `run.log` under `Sts2TestLogDir`. The default directory is `/tmp/sts2-combat-tests/<test-mod-id>/logs`; MSBuild stdout only replays output after the test-start marker, so inspect the full log for startup noise.

The default settings path is isolated under `/tmp/sts2-combat-tests/<test-mod-id>`. CI jobs can keep profiles and settings separate by setting `Sts2TestXdgDataHome`:

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:RunSts2Tests \
  -p:Sts2Path=/home/ci/sts2 \
  -p:Sts2TestXdgDataHome=/tmp/sts2-tests/$CI_JOB_ID
```

## Sample Combat Test

This test starts an Ironclad fight against one Big Dummy, injects Strike into hand, plays it, and checks the enemy HP. It is a good first smoke test after wiring the package into a mod project. If it fails, check the STS2 path, mod manifest, dependency loading order, and initializer.

```csharp
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using TestTheSpire;
using Xunit;

namespace YourMod.Tests;

public sealed class StrikeTests : CombatTestSuite
{
    protected override void ConfigureBattle(CombatTestBattleBuilder battle)
    {
        battle
            .Player<Ironclad>()
            .AddEnemy<BigDummy>()
            .WithSeed("strike-sample");
    }

    [Fact]
    public async Task Strike_deals_six_damage()
    {
        var enemy = EnemyAt(0);
        var hpBefore = enemy.CurrentHp;
        var strike = await AddToHand<StrikeIronclad>();

        await Play(strike, enemy);

        Assert.Equal(hpBefore - 6, enemy.CurrentHp);
    }
}
```

Mod card tests follow the same path: prepare the battle, inject the target card, record enemy HP, player block, or pile counts, play the card, wait for the action queue to clear, and assert the exact state change.

## Maintain TestTheSpire

This section is for framework maintainers and NuGet publishers.

### Repository Layout

- `TestTheSpire.csproj`: NuGet package project.
- `Framework/`: runtime bootstrap, xUnit assertion runner, and combat helpers.
- `buildTransitive/`: MSBuild targets imported by test projects through `PackageReference`.
- `GameFolder.props`: STS2 path detection for this repository. Put machine-specific paths in `LocalSettings.props`.
- `LICENSE`: LGPL-3.0 license text.

### Build The Package

Build and pack from this repository root:

```bash
dotnet build TestTheSpire.csproj -c Debug

dotnet pack TestTheSpire.csproj -c Release
```

The NuGet package is written to:

```text
artifacts/packages/TestTheSpire.0.1.4.nupkg
```

For nuget.org:

```bash
dotnet nuget push artifacts/packages/TestTheSpire.0.1.4.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate
```

NuGet versions cannot be overwritten. Change `<Version>` in `TestTheSpire.csproj` before the next publish.
