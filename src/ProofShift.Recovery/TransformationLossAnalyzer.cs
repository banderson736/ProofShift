using ProofShift.Domain;

namespace ProofShift.Recovery;

public sealed record TransformationLossResult(bool IsReversible, string Reason);

public static class TransformationLossAnalyzer
{
    public static TransformationLossResult Analyze(MigrationEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (edge.Operation.IsDestructive || edge.Operation.Parameters.GetValueOrDefault("lossy") == "true")
            return Loss("The migration edge is explicitly marked destructive or lossy.");
        if (edge.Operation.Type is MigrationOperationType.Merge or MigrationOperationType.Aggregate or
            MigrationOperationType.Derive or MigrationOperationType.Exclude)
            return Loss($"Operation '{edge.Operation.Type}' cannot be inverted without retained source information.");

        foreach (var step in edge.Operation.Steps)
        {
            var result = AnalyzeStep(step);
            if (!result.IsReversible) return result;
        }
        foreach (var field in edge.Operation.Fields)
        foreach (var step in field.Pipeline)
        {
            var result = AnalyzeStep(step);
            if (!result.IsReversible) return result;
        }

        return new TransformationLossResult(true, "The graph operation and configured field pipelines have a provable inverse.");
    }

    private static TransformationLossResult AnalyzeStep(TransformationStep step)
    {
        if (step.Version != "1") return Loss($"Transformation '{step.Type}' version '{step.Version}' has no registered inverse analyzer.");
        return step.Type switch
        {
            TransformationStepType.Copy or TransformationStepType.Rename =>
                new TransformationLossResult(true, "Copy and rename preserve the source value."),
            TransformationStepType.CodeMap => AnalyzeCodeMap(step),
            TransformationStepType.Trim => Loss("Trim discards leading or trailing source characters."),
            TransformationStepType.NormalizeString => Loss("String normalization discards source casing and normalization form."),
            TransformationStepType.NormalizeDate => Loss("Date normalization does not preserve the original source representation."),
            TransformationStepType.Concatenate => Loss("Concatenation is not provably invertible for arbitrary source fields."),
            TransformationStepType.Split => Loss("Split is not provably invertible for arbitrary source values and delimiters."),
            TransformationStepType.Lookup => Loss("Lookup output does not retain a guaranteed inverse key."),
            TransformationStepType.Calculate => Loss("Calculated output does not retain all calculation inputs."),
            TransformationStepType.Archive => Loss("Archive transformation does not prove source reconstruction."),
            TransformationStepType.Exclude => Loss("Exclusion produces no target value to reverse."),
            _ => Loss($"Transformation '{step.Type}' has no registered inverse analyzer.")
        };
    }

    private static TransformationLossResult AnalyzeCodeMap(TransformationStep step)
    {
        var outputs = step.Parameters
            .Where(pair => pair.Key is not ("unknown" or "default" or "defaultValue"))
            .Select(pair => pair.Value).ToList();
        if (step.Parameters.GetValueOrDefault("unknown") == "default" &&
            step.Parameters.TryGetValue("defaultValue", out var defaultValue))
            outputs.Add(defaultValue);
        if (step.Parameters.GetValueOrDefault("unknown") == "pass-through")
            return Loss("Code-map pass-through can collide with an explicitly mapped target value.");
        if (outputs.Count == 0 || outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Count)
            return Loss("Code map maps multiple source values to the same target value.");
        return new TransformationLossResult(true, "Code map has a unique target value for every configured source value.");
    }

    private static TransformationLossResult Loss(string reason) => new(false, reason);
}
