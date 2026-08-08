using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using Xunit;

namespace TestTheSpire;

internal sealed class CombatTestRunner
{
    private readonly NGame _game;

    public CombatTestRunner(NGame game)
    {
        _game = game;
    }

    public async Task<CombatTestRunSummary> RunAsync(Assembly assembly, string? nameFilter, bool listOnly)
    {
        var testCases = DiscoverTests(assembly, nameFilter);
        Log.Info(
            $"[{CombatTestBootstrap.LogPrefix}] START mode={(listOnly ? "list" : "run")} total={testCases.Count}{FormatFilter(nameFilter)}");

        if (listOnly)
        {
            foreach (var testCase in testCases) Log.Info($"[{CombatTestBootstrap.LogPrefix}] {testCase.DisplayName}");

            return new CombatTestRunSummary(testCases.Count, 0, 0, 0, Array.Empty<TestResult>());
        }

        List<TestResult> results = new();
        foreach (var testCase in testCases)
        {
            if (!string.IsNullOrWhiteSpace(testCase.SkipReason))
            {
                Log.Info($"[{CombatTestBootstrap.LogPrefix}] SKIP {testCase.DisplayName}: {testCase.SkipReason}");
                results.Add(TestResult.Skipped(testCase.DisplayName, testCase.SkipReason));
                continue;
            }

            var result = await RunSingleAsync(testCase);
            results.Add(result);
            Log.Info(
                $"[{CombatTestBootstrap.LogPrefix}] {result.Outcome.ToString().ToUpperInvariant()} {result.DisplayName} ({result.Elapsed.TotalMilliseconds:F0} ms)");
            if (result.Error != null) Log.Error($"[{CombatTestBootstrap.LogPrefix}] {result.Error}");
        }

        return CombatTestRunSummary.FromResults(results);
    }

    private static string FormatFilter(string? nameFilter)
    {
        return string.IsNullOrWhiteSpace(nameFilter) ? string.Empty : $" filter=\"{nameFilter}\"";
    }

    private async Task<TestResult> RunSingleAsync(DiscoveredTestCase testCase)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (testCase.NetworkLocalNetIds.Count > 0)
            {
                await RunNetworkChecksumCaseAsync(testCase);
            }
            else
            {
                await RunSinglePassAsync(testCase, testCase.BattleDefinition, captureChecksums: false);
            }

            stopwatch.Stop();
            return TestResult.Passed(testCase.DisplayName, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return TestResult.Failed(testCase.DisplayName, stopwatch.Elapsed, Unwrap(ex));
        }
    }

    private async Task<IReadOnlyList<RecordedChecksum>> RunSinglePassAsync(
        DiscoveredTestCase testCase,
        CombatTestBattleDefinition battleDefinition,
        bool captureChecksums)
    {
        CombatTestContext? context = null;
        CombatTestSuite? suite = null;
        var suiteDisposed = false;
        var contextReset = false;
        var zeroActionHandled = false;
        Action<NetChecksumData, string, NetFullCombatState>? recordChecksum = null;
        List<RecordedChecksum> checksums = new();

        try
        {
            suite = CreateSuite(testCase);
            var seed = battleDefinition.Seed ?? testCase.UniqueName;
            context = await CombatTestContext.CreateAsync(_game, battleDefinition, seed);
            context.BeginActionExecutionTracking();
            suite.AttachContext(context);

            if (captureChecksums)
            {
                context.EnableChecksumTracking();
                recordChecksum = (data, checksumContext, _) =>
                    checksums.Add(new RecordedChecksum(data.id, data.checksum, checksumContext));
                RunManager.Instance.ChecksumTracker.ChecksumGenerated += recordChecksum;
            }

            try
            {
                await suite.InitializeInternalAsync();
                await InvokeTestMethodAsync(testCase, suite);
            }
            finally
            {
                if (recordChecksum != null)
                    RunManager.Instance.ChecksumTracker.ChecksumGenerated -= recordChecksum;
            }

            zeroActionHandled = true;
            await EnsureCombatActionWasExecutedAsync(testCase, context);
            context.EndActionExecutionTracking();

            await suite.DisposeInternalAsync();
            suiteDisposed = true;
            await context.ResetAsync();
            contextReset = true;

            return checksums;
        }
        catch (Exception ex)
        {
            var actual = Unwrap(ex);
            if (!zeroActionHandled && context?.ExecutedActionCount == 0)
                try
                {
                    zeroActionHandled = true;
                    await EnsureCombatActionWasExecutedAsync(testCase, context, actual);
                }
                catch (Exception cleanupPreparationEx)
                {
                    actual = new AggregateException(actual, Unwrap(cleanupPreparationEx));
                }

            try
            {
                if (suite != null && !suiteDisposed)
                {
                    await suite.DisposeInternalAsync();
                    suiteDisposed = true;
                }
            }
            catch (Exception disposeEx)
            {
                actual = new AggregateException(actual, Unwrap(disposeEx));
            }

            try
            {
                if (context != null && !contextReset)
                {
                    await context.ResetAsync();
                    contextReset = true;
                }
            }
            catch (Exception resetEx)
            {
                actual = new AggregateException(actual, Unwrap(resetEx));
            }

            throw actual;
        }
    }

    private static async Task EnsureCombatActionWasExecutedAsync(
        DiscoveredTestCase testCase,
        CombatTestContext context,
        Exception? originalError = null)
    {
        if (context.ExecutedActionCount > 0) return;

        if (CombatTestBootstrap.ZeroActionBehavior == ZeroActionTestBehavior.EnqueueCleanupNoOp)
        {
            await context.EnsureActionExecutedForCleanupAsync(testCase.DisplayName);
            return;
        }

        TerminateZeroActionTest(testCase, originalError);
    }

    private static void TerminateZeroActionTest(
        DiscoveredTestCase testCase,
        Exception? originalError)
    {
        var message = new StringBuilder()
            .AppendLine(
                $"TERMINATE {testCase.DisplayName}: completed without executing any combat GameAction.")
            .Append(
                "Zero-action combat tests can crash STS2 headless cleanup; execute at least one action before the test returns.");

        if (originalError != null)
            message
                .AppendLine()
                .AppendLine("Original error before cleanup:")
                .Append(originalError);

        CombatTestBootstrap.TerminateWithError(message.ToString());
    }

    private async Task RunNetworkChecksumCaseAsync(DiscoveredTestCase testCase)
    {
        if (testCase.NetworkLocalNetIds.Count < 2)
            throw new InvalidOperationException(
                $"{testCase.DisplayName} uses NetworkChecksumFact but configured fewer than two local net IDs.");

        List<NetworkChecksumCapture> captures = new();
        foreach (var localNetId in testCase.NetworkLocalNetIds)
        {
            var battleDefinition = testCase.BattleDefinition.WithLocalNetId(localNetId);
            var checksums = await RunSinglePassAsync(testCase, battleDefinition, captureChecksums: true);
            captures.Add(new NetworkChecksumCapture(localNetId, checksums));
        }

        var mismatch = FindNetworkChecksumMismatch(testCase, captures);
        if (testCase.ExpectNetworkChecksumMismatch)
        {
            if (mismatch == null)
                throw new InvalidOperationException(
                    $"{testCase.DisplayName} was expected to reproduce a checksum mismatch, but all captures matched.");

            Log.Info($"[{CombatTestBootstrap.LogPrefix}] Expected checksum mismatch reproduced: {mismatch.Split('\n')[0]}");
            return;
        }

        if (mismatch != null) throw new InvalidOperationException(mismatch);
    }

    private static string? FindNetworkChecksumMismatch(
        DiscoveredTestCase testCase,
        IReadOnlyList<NetworkChecksumCapture> captures)
    {
        var expected = captures[0];
        if (expected.Checksums.Count == 0)
            return $"{testCase.DisplayName} produced no checksums for local net ID {expected.LocalNetId}.";

        foreach (var actual in captures.Skip(1))
        {
            if (actual.Checksums.Count == 0)
                return $"{testCase.DisplayName} produced no checksums for local net ID {actual.LocalNetId}.";

            if (actual.Checksums.Count != expected.Checksums.Count)
                return BuildChecksumCountMismatchMessage(testCase, expected, actual);

            for (var i = 0; i < expected.Checksums.Count; i++)
            {
                var expectedChecksum = expected.Checksums[i];
                var actualChecksum = actual.Checksums[i];
                if (expectedChecksum.Id != actualChecksum.Id
                    || NormalizeChecksumContext(expectedChecksum.Context) != NormalizeChecksumContext(actualChecksum.Context)
                    || (!testCase.CompareNetworkChecksumContextsOnly
                        && expectedChecksum.Checksum != actualChecksum.Checksum))
                    return BuildChecksumMismatchMessage(testCase, expected, actual, i);
            }
        }

        return null;
    }

    private static CombatTestSuite CreateSuite(DiscoveredTestCase testCase)
    {
        if (Activator.CreateInstance(testCase.SuiteType) is not CombatTestSuite suite)
            throw new InvalidOperationException(
                $"Could not create test suite {testCase.SuiteType.FullName}. Ensure it has a parameterless constructor.");

        return suite;
    }

    private static async Task InvokeTestMethodAsync(DiscoveredTestCase testCase, CombatTestSuite suite)
    {
        var returnValue = testCase.Method.Invoke(suite, testCase.Arguments);
        switch (returnValue)
        {
            case null when testCase.Method.ReturnType == typeof(void):
                return;
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                throw new InvalidOperationException(
                    $"{testCase.Method.DeclaringType?.FullName}.{testCase.Method.Name} must return void, Task, or ValueTask.");
        }
    }

    private static List<DiscoveredTestCase> DiscoverTests(Assembly assembly, string? nameFilter)
    {
        List<DiscoveredTestCase> discovered = new();

        foreach (var suiteType in assembly
                     .GetTypes()
                     .Where(static type => !type.IsAbstract && typeof(CombatTestSuite).IsAssignableFrom(type))
                     .OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            var suite = CreateSuiteForDiscovery(suiteType);
            var battleDefinition = suite.BuildBattleDefinition();

            foreach (var method in suiteType
                         .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .OrderBy(static method => method.Name, StringComparer.Ordinal))
            {
                var theory = method.GetCustomAttribute<TheoryAttribute>();
                var networkChecksumFact = method.GetCustomAttribute<NetworkChecksumFactAttribute>();
                var fact = method.GetCustomAttribute<FactAttribute>();

                if (theory == null && fact == null) continue;

                if (theory != null)
                {
                    var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToArray();
                    if (inlineData.Length == 0)
                    {
                        discovered.Add(BuildCase(
                            suiteType,
                            method,
                            theory,
                            battleDefinition,
                            Array.Empty<object?>(),
                            "Theory has no InlineData."));
                        continue;
                    }

                    for (var i = 0; i < inlineData.Length; i++)
                    {
                        var arguments = inlineData[i].Data;
                        var displayName = BuildDisplayName(suiteType, method, theory, arguments);
                        discovered.Add(new DiscoveredTestCase(
                            suiteType,
                            method,
                            battleDefinition,
                            $"{suiteType.FullName}.{method.Name}[{i}]",
                            displayName,
                            arguments,
                            theory.Skip,
                            Array.Empty<ulong>()));
                    }
                }
                else if (networkChecksumFact != null)
                {
                    discovered.Add(BuildNetworkChecksumCase(
                        suiteType,
                        method,
                        networkChecksumFact,
                        battleDefinition,
                        networkChecksumFact.Skip));
                }
                else if (fact != null)
                {
                    discovered.Add(BuildCase(
                        suiteType,
                        method,
                        fact,
                        battleDefinition,
                        Array.Empty<object?>(),
                        fact.Skip));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
            discovered = discovered
                .Where(test => test.DisplayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return discovered;
    }

    private static CombatTestSuite CreateSuiteForDiscovery(Type suiteType)
    {
        if (Activator.CreateInstance(suiteType) is not CombatTestSuite suite)
            throw new InvalidOperationException(
                $"Could not create test suite {suiteType.FullName} during discovery. Ensure it has a parameterless constructor.");

        return suite;
    }

    private static DiscoveredTestCase BuildCase(
        Type suiteType,
        MethodInfo method,
        FactAttribute attribute,
        CombatTestBattleDefinition battleDefinition,
        object?[] arguments,
        string? skipReason)
    {
        return new DiscoveredTestCase(
            suiteType,
            method,
            battleDefinition,
            $"{suiteType.FullName}.{method.Name}",
            BuildDisplayName(suiteType, method, attribute, arguments),
            arguments,
            skipReason,
            Array.Empty<ulong>());
    }

    private static DiscoveredTestCase BuildNetworkChecksumCase(
        Type suiteType,
        MethodInfo method,
        NetworkChecksumFactAttribute attribute,
        CombatTestBattleDefinition battleDefinition,
        string? skipReason)
    {
        var localNetIds = attribute.LocalNetIds
            .Select(static netId =>
            {
                if (netId <= 0)
                    throw new InvalidOperationException(
                        $"NetworkChecksumFact local net IDs must be positive. Found {netId}.");
                return (ulong)netId;
            })
            .ToArray();

        return new DiscoveredTestCase(
            suiteType,
            method,
            battleDefinition,
            $"{suiteType.FullName}.{method.Name}",
            BuildDisplayName(suiteType, method, attribute, Array.Empty<object?>()),
            Array.Empty<object?>(),
            skipReason,
            localNetIds,
            attribute.ExpectMismatch,
            attribute.CompareContextsOnly);
    }

    private static string BuildDisplayName(
        Type suiteType,
        MethodInfo method,
        FactAttribute attribute,
        IReadOnlyList<object?> arguments)
    {
        if (!string.IsNullOrWhiteSpace(attribute.DisplayName)) return attribute.DisplayName!;

        if (arguments.Count == 0) return $"{suiteType.Name}.{method.Name}";

        StringBuilder builder = new();
        builder.Append(suiteType.Name);
        builder.Append('.');
        builder.Append(method.Name);
        builder.Append('(');
        builder.Append(string.Join(", ", arguments.Select(FormatArgument)));
        builder.Append(')');
        return builder.ToString();
    }

    private static string FormatArgument(object? argument)
    {
        return argument switch
        {
            null => "null",
            string text => $"\"{text}\"",
            _ => argument.ToString() ?? argument.GetType().Name
        };
    }

    private static string BuildChecksumCountMismatchMessage(
        DiscoveredTestCase testCase,
        NetworkChecksumCapture expected,
        NetworkChecksumCapture actual)
    {
        return new StringBuilder()
            .AppendLine($"{testCase.DisplayName} generated different checksum counts.")
            .AppendLine(
                $"localNetId {expected.LocalNetId}: {expected.Checksums.Count} checksums [{FormatChecksumContexts(expected.Checksums)}]")
            .AppendLine(
                $"localNetId {actual.LocalNetId}: {actual.Checksums.Count} checksums [{FormatChecksumContexts(actual.Checksums)}]")
            .ToString();
    }

    private static string BuildChecksumMismatchMessage(
        DiscoveredTestCase testCase,
        NetworkChecksumCapture expected,
        NetworkChecksumCapture actual,
        int index)
    {
        var expectedChecksum = expected.Checksums[index];
        var actualChecksum = actual.Checksums[index];
        return new StringBuilder()
            .AppendLine($"{testCase.DisplayName} generated different checksums at index {index}.")
            .AppendLine(
                $"localNetId {expected.LocalNetId}: id={expectedChecksum.Id} checksum={expectedChecksum.Checksum} context={expectedChecksum.Context}")
            .AppendLine(
                $"localNetId {actual.LocalNetId}: id={actualChecksum.Id} checksum={actualChecksum.Checksum} context={actualChecksum.Context}")
            .ToString();
    }

    private static string FormatChecksumContexts(IReadOnlyList<RecordedChecksum> checksums)
    {
        return string.Join(" | ", checksums.Select(static checksum => $"{checksum.Id}:{checksum.Context}"));
    }

    private static string NormalizeChecksumContext(string context)
    {
        return Regex.Replace(context, @"\(\d+\)", "(*)");
    }

    private static Exception Unwrap(Exception ex)
    {
        if (ex is TargetInvocationException tie && tie.InnerException is Exception inner) return Unwrap(inner);

        if (ex is AggregateException aggregate
            && aggregate.InnerExceptions.Count == 1
            && aggregate.InnerExceptions[0] is Exception single)
            return Unwrap(single);

        return ex;
    }

    private sealed record DiscoveredTestCase(
        Type SuiteType,
        MethodInfo Method,
        CombatTestBattleDefinition BattleDefinition,
        string UniqueName,
        string DisplayName,
        object?[] Arguments,
        string? SkipReason,
        IReadOnlyList<ulong> NetworkLocalNetIds,
        bool ExpectNetworkChecksumMismatch = false,
        bool CompareNetworkChecksumContextsOnly = false);

    private sealed record NetworkChecksumCapture(
        ulong LocalNetId,
        IReadOnlyList<RecordedChecksum> Checksums);

    private sealed record RecordedChecksum(
        uint Id,
        uint Checksum,
        string Context);

    internal sealed record TestResult(
        string DisplayName,
        TestOutcome Outcome,
        TimeSpan Elapsed,
        string? SkipReason,
        string? Error)
    {
        public static TestResult Passed(string displayName, TimeSpan elapsed)
        {
            return new TestResult(displayName, TestOutcome.Passed, elapsed, null, null);
        }

        public static TestResult Failed(string displayName, TimeSpan elapsed, Exception error)
        {
            return new TestResult(displayName, TestOutcome.Failed, elapsed, null, error.ToString());
        }

        public static TestResult Skipped(string displayName, string? skipReason)
        {
            return new TestResult(displayName, TestOutcome.Skipped, TimeSpan.Zero, skipReason, null);
        }
    }

    internal enum TestOutcome
    {
        Passed,
        Failed,
        Skipped
    }
}

internal sealed record CombatTestRunSummary(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<CombatTestRunner.TestResult> Results)
{
    public static CombatTestRunSummary FromResults(IReadOnlyList<CombatTestRunner.TestResult> results)
    {
        return new CombatTestRunSummary(
            results.Count,
            results.Count(static result => result.Outcome == CombatTestRunner.TestOutcome.Passed),
            results.Count(static result => result.Outcome == CombatTestRunner.TestOutcome.Failed),
            results.Count(static result => result.Outcome == CombatTestRunner.TestOutcome.Skipped),
            results);
    }
}
