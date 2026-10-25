using System.Text.Json;
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
        if (args.Length != 6 || args[0] != "run" || args[2] != "--workspace" || args[4] != "--result-file")
        {
            Console.Error.WriteLine("Usage: ProofShift.DemoRunner run <scenario-id> --workspace <path> --result-file <path>");
            return 2;
        }

        var workspace = Path.GetFullPath(args[3]);
        if (Path.IsPathRooted(args[5]))
        {
            Console.Error.WriteLine("The result file must be a workspace-relative path.");
            return 2;
        }
        var resultPath = Path.GetFullPath(args[5], workspace);
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
            var scenario = DefaultDemoScenarios.CreateRegistry().GetRequired(args[1]);
            var result = await scenario.ExecuteAsync(new DemoScenarioExecutionContext(
                workspace, resultPath, cancellation.Token, PreserveArtifacts: true)).ConfigureAwait(false);
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
            Console.Error.WriteLine($"Demo scenario failed unexpectedly ({exception.GetType().Name}). " +
                "Exception details were withheld to protect configured connection values.");
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
