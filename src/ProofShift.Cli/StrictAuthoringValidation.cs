using ProofShift.Configuration;

namespace ProofShift.Cli;

internal static class StrictAuthoringValidation
{
    internal static IEnumerable<ConfigurationValidationIssue> UnapprovedMappings(LoadedProjectConfiguration configuration)
    {
        if (configuration.MigrationGraphConfiguration is not { } graph) yield break;
        foreach (var issue in Visit(graph.Document, graph.RelativePath, string.Empty)) yield return issue;
    }

    private static IEnumerable<ConfigurationValidationIssue> Visit(ConfigurationDocumentNode node, string file, string path)
    {
        if (node is ConfigurationMappingNode mapping)
        {
            if (mapping.Values.TryGetValue("approval", out var state) && state is ConfigurationScalarNode approval &&
                approval.Value != "confirmed" && (!mapping.Values.TryGetValue("required", out var required) ||
                    required is not ConfigurationScalarNode { Value: "false" }))
                yield return new ConfigurationValidationIssue("PSAUTHOR001", ValidationSeverity.Error,
                    "Required mapping has not been explicitly confirmed.", file, path, approval.Line, approval.Column);
            foreach (var pair in mapping.Values)
                foreach (var issue in Visit(pair.Value, file, string.IsNullOrEmpty(path) ? pair.Key : $"{path}.{pair.Key}"))
                    yield return issue;
        }
        if (node is ConfigurationSequenceNode sequence)
            for (var index = 0; index < sequence.Values.Count; index++)
                foreach (var issue in Visit(sequence.Values[index], file, $"{path}[{index}]")) yield return issue;
    }
}