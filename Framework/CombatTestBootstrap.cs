using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace TestTheSpire;

public sealed class CombatTestOptions
{
    public string LogPrefix { get; init; } = "TestTheSpire";

    public string PrimaryArg { get; init; } = "sts2-test";

    public string? LegacyArg { get; init; } = "sts2-test-demo";
}

public static class CombatTestBootstrap
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static Assembly? _testAssembly;
    private static CombatTestOptions _options = new();
    private static bool _initialized;
    private static bool _started;

    internal static string LogPrefix => _options.LogPrefix;

    public static bool Enabled
        => CommandLineHelper.HasArg(_options.PrimaryArg)
           || (!string.IsNullOrWhiteSpace(_options.LegacyArg) && CommandLineHelper.HasArg(_options.LegacyArg));

    public static void Initialize(Assembly testAssembly, CombatTestOptions? options = null)
    {
        _testAssembly = testAssembly ?? throw new ArgumentNullException(nameof(testAssembly));
        _options = options ?? new CombatTestOptions();

        if (!Enabled || _initialized) return;

        _initialized = true;
        InstallLocalAssemblyResolver();
        TestMode.TurnOnInternal();

        if (NGame.Instance != null) NGame.Instance.StartOnMainMenu = false;

        _ = StartWhenReadyAsync();
    }

    private static async Task StartWhenReadyAsync()
    {
        try
        {
            await WaitFor(
                () => NGame.Instance?.RootSceneContainer != null && IsStartupReady(),
                "Game startup did not finish in time.");

            await StartAsync(NGame.Instance ?? throw new InvalidOperationException("NGame was not available."));
        }
        catch (Exception ex)
        {
            Log.Error($"[{LogPrefix}] Bootstrap FAIL\n{ex}");
            ImmediateExit(1);
        }
    }

    private static async Task StartAsync(NGame game)
    {
        if (_started) return;

        _started = true;

        try
        {
            if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
                Log.Warn(
                    $"[{LogPrefix}] Running without native Godot headless mode. Use --headless for CLI runs.");

            SaveManager.Instance.Progress.EnableFtues = false;

            CombatTestRunner runner = new(game);
            var filter = CommandLineHelper.GetValue("sts2-test-filter");
            var listOnly = CommandLineHelper.HasArg("sts2-test-list");
            var testAssembly = _testAssembly
                               ?? throw new InvalidOperationException("Combat test assembly was not configured.");
            var summary = await runner.RunAsync(testAssembly, filter, listOnly);

            Log.Info(
                $"[{LogPrefix}] SUMMARY total={summary.Total} passed={summary.Passed} failed={summary.Failed} skipped={summary.Skipped}");

            ImmediateExit(summary.Failed == 0 ? 0 : 1);
        }
        catch (Exception ex)
        {
            Log.Error($"[{LogPrefix}] FAIL\n{ex}");
            ImmediateExit(1);
        }
    }

    internal static void TerminateWithError(string message)
    {
        Log.Error($"[{LogPrefix}] {message}");
        ImmediateExit(1);
    }

    internal static void ImmediateExit(int status)
    {
        Console.Out.Flush();
        Console.Error.Flush();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TerminateProcess(GetCurrentProcess(), (uint)status))
                throw new InvalidOperationException("TerminateProcess failed.");
        }
        else
        {
            PosixExit(status);
        }

        throw new InvalidOperationException("Immediate process exit returned unexpectedly.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void PosixExit(int status);

    private static void InstallLocalAssemblyResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromModDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromModDirectory;
    }

    private static Assembly? ResolveFromModDirectory(object? _, ResolveEventArgs args)
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(assemblyLocation)) return null;

        var baseDirectory = Path.GetDirectoryName(assemblyLocation);
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

        var simpleName = new AssemblyName(args.Name).Name ?? string.Empty;
        var candidatePath = Path.Combine(baseDirectory, simpleName + ".dll");
        if (!File.Exists(candidatePath)) return null;

        var loadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
        return loadContext?.LoadFromAssemblyPath(candidatePath);
    }

    private static bool IsStartupReady()
    {
        if (SaveManager.Instance.Progress == null || SaveManager.Instance.PrefsSave == null) return false;

        try
        {
            _ = SaveManager.Instance.CurrentProfileId;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitFor(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(timeoutMessage);

            var tree = (SceneTree)Engine.GetMainLoop();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}

[ModInitializer(nameof(Init))]
public static class TestTheSpireMod
{
    public static void Init()
    {
        Log.Info("[TestTheSpire] Loaded");
    }
}
