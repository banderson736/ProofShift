using System.Text.Json;
using ProofShift.DemoContracts;
using ProofShift.DemoScenarios;

return await DemoRunnerProgram.RunAsync(args);

internal static class DemoRunnerProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8 || args[0] != "--content-root" || args[2] != "run" ||
            args[4] != "--workspace" || args[6] != "--result-file")
        {
            Console.Error.WriteLine("Usage: ProofShift.DemoRunner --content-root <path> run <scenario-id> " +
                "--workspace <path> --result-file <path>");
            return 2;
        }

        var contentRoot = Path.GetFullPath(args[1]);
        var workspace = Path.GetFullPath(args[5]);
        if (Path.IsPathRooted(args[7]))
        {
            Console.Error.WriteLine("The result file must be a workspace-relative path.");
            return 2;
        }
        var resultPath = Path.GetFullPath(args[7], workspace);
        if (!IsWithin(workspace, resultPath))
        {
            Console.Error.WriteLine("The result file must be located inside the scenario workspace.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var scenario = DefaultDemoScenarios.CreateRegistry().GetRequired(args[3]);
            var result = await scenario.ExecuteAsync(new DemoScenarioExecutionContext(
                workspace, resultPath, contentRoot, cancellation.Token, PreserveArtifacts: true)).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result.Data, JsonOptions),
                cancellation.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.ChangeExtension(resultPath, ".txt"),
                result.HumanReadable, cancellation.Token).ConfigureAwait(false);
            Console.Write(result.HumanReadable);
            return 0;
        }
        catch (DemoScenarioUnavailableException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (KeyNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Demo scenario execution was cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            var failureLocation = new System.Diagnostics.StackTrace(exception, false).GetFrames()?
                .Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType?.Assembly == typeof(IDemoScenario).Assembly)
                .Select(method => $"{method!.DeclaringType!.FullName}.{method.Name}")
                .Take(3)
                .ToArray();
            Console.Error.WriteLine(SafeDemoRunnerDiagnostic.Format(exception.GetType().Name,
                failureLocation ?? []));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && !Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
