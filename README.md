# TestTheSpire

[![NuGet](https://img.shields.io/nuget/v/TestTheSpire.svg)](https://www.nuget.org/packages/TestTheSpire/0.1.1)

TestTheSpire 是一个用于 Slay the Spire 2 的测试框架。利用这个框架，可以在纯命令行环境下，不通过 steam 启动游戏直接进行测试。这使得通过 AI 完成完整的 代码编写->测试->Review 回环成为可能，从而提升 AI 代码编写的效率与可靠性，使得长时间执行卡牌编写任务成为可能。

这个项目依赖本机 STS2 本体。编译时会引用 `sts2.dll` 和 `GodotSharp.dll`，运行测试时会启动 STS2 可执行文件，并使用 headless 参数进入测试流程。

English Readme: [README.en.md](README.en.md)。

## 哪些项目使用了 TestTheSpire

 - [Shadowverse Beyond Mod](https://www.nexusmods.com/slaythespire2/mods/977?tab=description)

## 如何在自己的项目中使用 TestTheSpire

**目前经过验证的环境包括 WSL2/Linux 下，在 Windows 下理论上可以运行，不过需要一些不同的配置，我们正努力增加支持，请耐心等待。**

**你可能需要了解如何使用 Steamcmd 在 linux 环境下获取 Slay the spire 2**

先准备一个独立的测试项目，例如 `YourMod.Tests/YourMod.Tests.csproj`。测试项目通常引用被测 mod 项目，再通过 NuGet 引入 TestTheSpire：

```bash
dotnet add YourMod.Tests/YourMod.Tests.csproj package TestTheSpire --version 0.1.1
```

一个最小项目文件可以这样写：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>yourmod_tests</AssemblyName>
    <RootNamespace>YourMod.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../yourmod.csproj" />
    <PackageReference Include="TestTheSpire" Version="0.1.1" />
  </ItemGroup>
</Project>
```

本地验证还没有发布的 TestTheSpire 包时，先在 TestTheSpire 仓库执行 `dotnet pack`，再让测试项目从本地包目录 restore：

```xml
<RestoreSources>../TestTheSpire/artifacts/packages;$(RestoreSources)</RestoreSources>
```

测试程序集需要一个 STS2 mod initializer。这个 initializer 把当前测试程序集交给 TestTheSpire，xUnit fact 的发现和执行都会从这个程序集开始：

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

项目保留静态 manifest 时，把 `TestTheSpire` 放在被测 mod 之前。STS2 loader 会按依赖顺序加载 `xunit.v3.assert`、`TestTheSpire`、被测 mod、测试 mod：

```json
{
  "id": "yourmod_tests",
  "name": "yourmod.Tests",
  "author": "your team",
  "description": "Headless combat tests for yourmod.",
  "version": "0.1.1",
  "has_pck": false,
  "has_dll": true,
  "dependencies": [
    { "id": "TestTheSpire", "min_version": "0.1.1" },
    { "id": "yourmod", "min_version": null }
  ],
  "affects_gameplay": true
}
```

也可以让 MSBuild 在 `CopySts2TestPayload` 阶段生成测试 manifest。生成逻辑会读取 `$(Sts2TestModId)`、`$(Sts2MainModId)` 和测试程序集名。

## 配置 STS2 路径

运行测试时传入 STS2 根目录：

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:ListSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2
```

标准 Steam 安装通常可以让项目里的 `GameFolder.props` 推导路径。团队里常见的断点在路径不一致：有人把 STS2 放在独立磁盘，有人用解包目录跑测试，CI 也可能只挂载游戏目录。遇到这种情况，直接传属性最清楚：

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:RunSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2
```

CI 镜像如果只挂载了数据目录，可以直接传 `Sts2DataDir`。本地如果需要指定可执行文件或 mods 目录，可以传 `Sts2Executable` 和 `Sts2ModsDir`。

## 运行测试

只列出测试名，适合确认 STS2 已经正确加载测试 mod：

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:ListSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2
```

开发者正在改一张卡时，通常先跑一个筛选测试。失败输出会停在具体 fact，MR 评审也能看到这次改动影响的是哪条战斗路径：

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:RunSts2Tests \
  -p:Sts2Path=/home/me/games/slay-the-spire-2 \
  -p:Sts2TestArgs=--sts2-test-filter=Strike_deals_six_damage
```

常用 target：

- `CopySts2TestPayload`：构建测试程序集，写出测试 mod payload。
- `InstallSts2TestMod`：把 `TestTheSpire`、`xunit.v3.assert` 和测试 mod 复制到 STS2 `mods` 目录。
- `ListSts2Tests`：启动 headless STS2，打印发现到的 xUnit facts。
- `RunSts2Tests`：启动 headless STS2，执行匹配到的测试。

默认 settings 写到 `/tmp/sts2-combat-tests/<test-mod-id>`。CI 里多条 job 同时跑测试时，用 `Sts2TestXdgDataHome` 隔离 profile 和 settings：

```bash
dotnet msbuild YourMod.Tests/YourMod.Tests.csproj \
  -restore \
  -t:RunSts2Tests \
  -p:Sts2Path=/home/ci/sts2 \
  -p:Sts2TestXdgDataHome=/tmp/sts2-tests/$CI_JOB_ID
```

## 样例用例

下面的测试启动一场 Ironclad 对 Big Dummy 的战斗，把 Strike 加到手牌，打出后检查敌人 HP。这个用例适合作为接入后的第一条 smoke test：如果它失败，问题大概率在 STS2 路径、mod manifest、依赖加载顺序或 initializer。

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

mod 卡牌测试沿用同一条路径：准备战斗，注入目标卡牌，记录敌人 HP、玩家格挡或牌堆数量，打出卡牌，等待 action queue 清空后断言状态变化。这样失败日志会落在具体战斗状态上，开发者可以直接回到卡牌实现或 hook 里修正。

## 维护 TestTheSpire

这一节面向维护框架和发布 NuGet 包的人。

### 目录

- `TestTheSpire.csproj`：NuGet 包项目。
- `Framework/`：mod 初始化、xUnit 断言运行器、战斗上下文和测试辅助方法。
- `buildTransitive/`：通过 `PackageReference` 自动导入到测试项目的 MSBuild targets。
- `GameFolder.props`：TestTheSpire 仓库内的 STS2 路径推导。个人机器路径可以写到 `LocalSettings.props`。
- `LICENSE`：LGPL-3.0 license 文本。

### 构建和打包

在 TestTheSpire 仓库根目录执行：

```bash
dotnet build TestTheSpire.csproj -c Debug

dotnet pack TestTheSpire.csproj -c Release
```

NuGet 包会生成到：

```text
artifacts/packages/TestTheSpire.0.1.1.nupkg
```

发布到 nuget.org：

```bash
dotnet nuget push artifacts/packages/TestTheSpire.0.1.1.nupkg \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate
```

NuGet 版本号发布后无法覆盖。下一次发布前先修改 `TestTheSpire.csproj` 里的 `<Version>`。
