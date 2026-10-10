using ProofShift.Domain;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProofShift.Verification;

public sealed record VerificationWorksetKey(VerificationArtifactRole Role, string SemanticType);

public sealed record VerificationRuleWorkset(
    IVerificationRule Rule,
    string RuleType,
    VerificationScope Scope,
    DomainList<string> RequiredSemanticTypes,
    DomainList<string> RequiredSourceFields,
    DomainList<string> RequiredTargetFields,
    DomainList<VerificationOrderingKey> GroupingKeys,
    DomainList<VerificationOrderingKey> OrderingKeys,
    DomainList<VerificationOrderingKey> LookupKeys,
    VerificationPartitionExecution PartitionExecution,
    VerificationPartitionKeyDefinition? PartitionKey,
    DomainList<VerificationOrderingKey> ResolvedPartitionKeyFields,
    string? PartitionFallbackReason);

public sealed record VerificationIndexRequirement(string Name, DomainList<VerificationArtifactRole> Roles,
    DomainList<string> SemanticTypes, DomainList<string> Fields);

public sealed record VerificationExecutionPlan
{
    public DomainList<VerificationRuleWorkset> Rules { get; }
    public DomainList<VerificationOrderingKey> RequiredOrderingKeys { get; }
    public DomainList<VerificationIndexRequirement> RequiredIndexes { get; }
    public string ExpectedRequirementsFingerprint { get; }
    public string PartitionContractFingerprint { get; }
    public VerificationPartitionBasis ScratchPartitionBasis { get; } = VerificationPartitionBasis.ArtifactIdentity;

    public int AssignArtifactPartition(string nodeKey, ArtifactId artifactId, int partitionCount) =>
        VerificationPartitioning.Assign(ScratchPartitionBasis, nodeKey, [new StringValue(artifactId.Value)], partitionCount);

    public int AssignTargetIdentityPartition(string nodeKey, string identity, int partitionCount) =>
        VerificationPartitioning.AssignArtifactIdentity(ScratchPartitionBasis, nodeKey, identity, partitionCount);
    private readonly IReadOnlyDictionary<VerificationWorksetKey, DomainList<string>> _requiredFields;
    private readonly IReadOnlyDictionary<VerificationWorksetKey, DomainList<VerificationOrderingKey>> _requiredKeys;

    private VerificationExecutionPlan(IEnumerable<VerificationRuleWorkset> rules,
        IReadOnlyDictionary<VerificationWorksetKey, HashSet<string>> requiredFields,
        IReadOnlyDictionary<VerificationWorksetKey, HashSet<VerificationOrderingKey>> requiredKeys)
    {
        Rules = new DomainList<VerificationRuleWorkset>(rules.OrderBy(item => item.Rule.Id.Value, StringComparer.Ordinal));
        PartitionContractFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "proofshift-verification-partition-contract-v1",
            hashVersion = VerificationPartitioning.Version,
            rules = Rules.Select(rule => new
            {
                id = rule.Rule.Id.Value,
                type = rule.RuleType,
                mode = rule.PartitionExecution.ToString(),
                fallbackReason = rule.PartitionFallbackReason,
                basis = rule.PartitionKey?.Basis.ToString(),
                role = rule.PartitionKey?.Role.ToString(),
                fieldOptions = rule.PartitionKey?.FieldOptions.ToArray() ?? [],
                fields = rule.ResolvedPartitionKeyFields.ToArray()
            })
        }))).ToLowerInvariant();
        RequiredOrderingKeys = new DomainList<VerificationOrderingKey>(Rules
            .SelectMany(item => item.GroupingKeys.Concat(item.OrderingKeys).Concat(item.LookupKeys))
            .Distinct().OrderBy(key => key.SemanticType, StringComparer.Ordinal)
            .ThenBy(key => key.Field, StringComparer.Ordinal).ThenBy(key => key.Role).ThenBy(key => key.Descending));
        _requiredFields = new ReadOnlyDictionary<VerificationWorksetKey, DomainList<string>>(requiredFields.ToDictionary(
            pair => pair.Key, pair => new DomainList<string>(pair.Value.Order(StringComparer.Ordinal))));
        _requiredKeys = new ReadOnlyDictionary<VerificationWorksetKey, DomainList<VerificationOrderingKey>>(requiredKeys.ToDictionary(
            pair => pair.Key, pair => OrderedKeys(pair.Value)));
        RequiredIndexes = requiredKeys.Values.All(keys => keys.Count == 0)
            ? new DomainList<VerificationIndexRequirement>([])
            : new DomainList<VerificationIndexRequirement>([
                new VerificationIndexRequirement("artifact_order_key_idx",
                    new DomainList<VerificationArtifactRole>(requiredKeys.Keys.Select(key => key.Role).Distinct().Order()),
                    new DomainList<string>(requiredKeys.Keys.Select(key => key.SemanticType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    new DomainList<string>(requiredKeys.Values.SelectMany(keys => keys).Select(key => key.Field)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            ]);
        var expectedSchema = new
        {
            version = "proofshift-expected-workset-schema-v2",
            partitionBucketVersion = VerificationPartitioning.Version,
            partitionBucketCount = VerificationPartitioning.StableBucketCount,
            fields = _requiredFields.Where(pair => pair.Key.Role != VerificationArtifactRole.ActualTarget)
                .OrderBy(pair => pair.Key.Role).ThenBy(pair => pair.Key.SemanticType, StringComparer.Ordinal)
                .Select(pair => new { role = pair.Key.Role.ToString(), semanticType = pair.Key.SemanticType, fields = pair.Value.ToArray() }),
            keys = _requiredKeys.Where(pair => pair.Key.Role != VerificationArtifactRole.ActualTarget)
                .OrderBy(pair => pair.Key.Role).ThenBy(pair => pair.Key.SemanticType, StringComparer.Ordinal)
                .Select(pair => new { role = pair.Key.Role.ToString(), semanticType = pair.Key.SemanticType, keys = pair.Value.ToArray() })
        };
        ExpectedRequirementsFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(expectedSchema))).ToLowerInvariant();
    }

    public static VerificationExecutionPlan Create(VerificationRuleSet ruleSet, MigrationGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        var worksets = new List<VerificationRuleWorkset>(ruleSet.Rules.Count);
        var fieldsByWorkset = new Dictionary<VerificationWorksetKey, HashSet<string>>();
        var keysByWorkset = new Dictionary<VerificationWorksetKey, HashSet<VerificationOrderingKey>>();
        foreach (var rule in ruleSet.Rules)
        {
            var definition = ruleSet.Definitions.Single(item => item.Id == rule.Id);
            var descriptor = ruleSet.DescriptorsByRuleId[rule.Id.Value];
            if (descriptor.PartitionExecution == VerificationPartitionExecution.PartitionPartialWithGlobalMerge &&
                rule is not IVerificationPartitionFindingMerger)
                throw new VerificationRuleException("PSRULE012",
                    $"Rule '{rule.Id.Value}' declares partial partition execution without a deterministic global finding merger.");
            var unboundFieldOption = descriptor.Options.FirstOrDefault(option =>
                (option.Kind == RuleOptionKind.FieldReference ||
                 option.Kind == RuleOptionKind.Sequence && option.ItemKind == RuleOptionKind.FieldReference) &&
                !descriptor.FieldRequirements.Any(requirement => requirement.OptionName == option.Name));
            if (unboundFieldOption is not null)
                throw new VerificationRuleException("PSRULE008",
                    $"Rule '{rule.Id.Value}' field option '{unboundFieldOption.Name}' has no side-aware workset requirement.");
            var runtimeKeys = rule.RequiredOrderingKeys;
            var groupingKeys = descriptor.GroupingKeys.Concat(runtimeKeys.Where(key => key.Role == VerificationOrderingRole.Grouping)).ToList();
            var orderingKeys = descriptor.OrderingKeys.Concat(runtimeKeys.Where(key => key.Role == VerificationOrderingRole.Ordering)).ToList();
            var lookupKeys = descriptor.LookupKeys.Concat(runtimeKeys.Where(key => key.Role == VerificationOrderingRole.Lookup)).ToList();
            var semanticTypes = new HashSet<string>(descriptor.RequiredSemanticTypes, StringComparer.Ordinal);
            var sourceFields = new HashSet<string>(descriptor.RequiredSourceFields, StringComparer.Ordinal);
            var targetFields = new HashSet<string>(descriptor.RequiredTargetFields, StringComparer.Ordinal);
            AddFields(fieldsByWorkset, new VerificationWorksetKey(VerificationArtifactRole.Source, "*"), sourceFields);
            AddFields(fieldsByWorkset, new VerificationWorksetKey(VerificationArtifactRole.ExpectedTarget, "*"), targetFields);
            AddFields(fieldsByWorkset, new VerificationWorksetKey(VerificationArtifactRole.ActualTarget, "*"), targetFields);

            var resolvedBindings = new List<(string OptionName, string Field, VerificationFieldSide Side, string SemanticType)>();
            foreach (var requirement in descriptor.FieldRequirements)
            {
                var semanticType = ResolveSemanticType(definition, requirement);
                var optionIsSet = definition.StructuredOptions.TryGetValue(requirement.OptionName, out var optionValue);
                var fields = optionIsSet ? ReadFields(optionValue!, definition, requirement.OptionName) : requirement.DefaultFields.ToArray();
                if (!optionIsSet && requirement.IncludeMappedTargetFieldsWhenUnset)
                    fields = GetMappedTargetFields(graph, definition, requirement);
                if (fields.Length == 0) continue;
                var sideFields = requirement.Side == VerificationFieldSide.Source ? sourceFields : targetFields;
                foreach (var field in fields)
                {
                    sideFields.Add(field);
                    resolvedBindings.Add((requirement.OptionName, field, requirement.Side, semanticType));
                }
                if (semanticType != "*") semanticTypes.Add(semanticType);
                var artifactRoles = requirement.Side == VerificationFieldSide.Source
                    ? new[] { VerificationArtifactRole.Source }
                    : new[] { VerificationArtifactRole.ExpectedTarget, VerificationArtifactRole.ActualTarget };
                foreach (var artifactRole in artifactRoles)
                {
                    var worksetKey = new VerificationWorksetKey(artifactRole, semanticType);
                    AddFields(fieldsByWorkset, worksetKey, fields);
                    if (requirement.KeyRole is { } keyRole)
                    {
                        var boundKeys = fields.Select(field =>
                            new VerificationOrderingKey(semanticType == "*" ? string.Empty : semanticType, field, keyRole)).ToArray();
                        AddKeys(keysByWorkset, worksetKey, boundKeys);
                        switch (keyRole)
                        {
                            case VerificationOrderingRole.Grouping: groupingKeys.AddRange(boundKeys); break;
                            case VerificationOrderingRole.Ordering: orderingKeys.AddRange(boundKeys); break;
                            case VerificationOrderingRole.Lookup: lookupKeys.AddRange(boundKeys); break;
                        }
                    }
                }
            }

            var allKeys = OrderedKeys(groupingKeys.Concat(orderingKeys).Concat(lookupKeys));

            AddDescriptorKeys(descriptor.GroupingKeys, VerificationOrderingRole.Grouping, targetFields,
                VerificationArtifactRole.ExpectedTarget, fieldsByWorkset, keysByWorkset);
            AddDescriptorKeys(descriptor.GroupingKeys, VerificationOrderingRole.Grouping, targetFields,
                VerificationArtifactRole.ActualTarget, fieldsByWorkset, keysByWorkset);
            AddDescriptorKeys(descriptor.OrderingKeys, VerificationOrderingRole.Ordering, targetFields,
                VerificationArtifactRole.ExpectedTarget, fieldsByWorkset, keysByWorkset);
            AddDescriptorKeys(descriptor.OrderingKeys, VerificationOrderingRole.Ordering, targetFields,
                VerificationArtifactRole.ActualTarget, fieldsByWorkset, keysByWorkset);
            AddDescriptorKeys(descriptor.LookupKeys, VerificationOrderingRole.Lookup, targetFields,
                VerificationArtifactRole.ExpectedTarget, fieldsByWorkset, keysByWorkset);
            AddDescriptorKeys(descriptor.LookupKeys, VerificationOrderingRole.Lookup, targetFields,
                VerificationArtifactRole.ActualTarget, fieldsByWorkset, keysByWorkset);

            foreach (var key in allKeys)
            {
                var matchingBindings = resolvedBindings.Where(binding => binding.Field == key.Field &&
                    (key.SemanticType.Length == 0 || binding.SemanticType == key.SemanticType || binding.SemanticType == "*"))
                    .ToArray();
                if (matchingBindings.Length == 0 && !descriptor.RequiredSourceFields.Contains(key.Field) &&
                    !descriptor.RequiredTargetFields.Contains(key.Field) &&
                    !descriptor.GroupingKeys.Concat(descriptor.OrderingKeys).Concat(descriptor.LookupKeys).Contains(key))
                    throw new VerificationRuleException("PSRULE008",
                        $"Rule '{rule.Id.Value}' orders by field '{key.Field}' without declaring it in its descriptor workset.");
                foreach (var binding in matchingBindings)
                {
                    var artifactRoles = binding.Side == VerificationFieldSide.Source
                        ? new[] { VerificationArtifactRole.Source }
                        : new[] { VerificationArtifactRole.ExpectedTarget, VerificationArtifactRole.ActualTarget };
                    foreach (var artifactRole in artifactRoles)
                    {
                        var worksetKey = new VerificationWorksetKey(artifactRole, binding.SemanticType);
                        AddKeys(keysByWorkset, worksetKey, [new VerificationOrderingKey(
                            binding.SemanticType == "*" ? string.Empty : binding.SemanticType, key.Field, key.Role, key.Descending)]);
                    }
                }
            }

            semanticTypes.UnionWith(allKeys.Where(key => key.SemanticType.Length > 0).Select(key => key.SemanticType));
            var partitionKeyFields = descriptor.PartitionKey is not { Basis: not VerificationPartitionBasis.ArtifactIdentity } partitionKey
                ? OrderedKeys([])
                : OrderedKeys(partitionKey.FieldOptions.SelectMany(optionName => resolvedBindings
                    .Where(binding => binding.OptionName == optionName &&
                        (partitionKey.Role == VerificationArtifactRole.Source
                            ? binding.Side == VerificationFieldSide.Source : binding.Side == VerificationFieldSide.Target))
                    .Select(binding => new VerificationOrderingKey(binding.SemanticType, binding.Field,
                        partitionKey.Basis == VerificationPartitionBasis.GroupingKey
                            ? VerificationOrderingRole.Grouping : VerificationOrderingRole.Lookup))));
            var resolvedExecution = descriptor.PartitionExecution;
            string? partitionFallbackReason = null;
            if (resolvedExecution != VerificationPartitionExecution.Global && descriptor.PartitionKey is { Role: not VerificationArtifactRole.ActualTarget })
            {
                resolvedExecution = VerificationPartitionExecution.Global;
                partitionFallbackReason = "The declared key role is not directly routed by the current target scratch router.";
            }
            if (resolvedExecution != VerificationPartitionExecution.Global &&
                descriptor.PartitionKey is { Basis: not (VerificationPartitionBasis.ArtifactIdentity or VerificationPartitionBasis.GroupingKey) })
            {
                resolvedExecution = VerificationPartitionExecution.Global;
                partitionFallbackReason = "The declared owner key basis is not supported by the current scratch router.";
            }
            if (resolvedExecution != VerificationPartitionExecution.Global &&
                descriptor.PartitionKey is { Basis: not VerificationPartitionBasis.ArtifactIdentity } && partitionKeyFields.Count == 0)
            {
                resolvedExecution = VerificationPartitionExecution.Global;
                partitionFallbackReason = "The declared partition field did not resolve to a side-aware workset field.";
            }
            if (descriptor.PartitionKey is { Basis: VerificationPartitionBasis.GroupingKey })
            {
                if (graph is null || partitionKeyFields.Count != 1)
                {
                    resolvedExecution = VerificationPartitionExecution.Global;
                    partitionFallbackReason = "Grouping locality requires a graph-proven single-field target identity.";
                }
                else
                {
                    var key = partitionKeyFields[0];
                    var targetNodeOption = definition.StructuredOptions.TryGetValue("targetNode", out var targetNodeValue) &&
                        targetNodeValue is StringValue nodeText ? nodeText.Value : null;
                    var targetNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Target &&
                        node.SemanticType == key.SemanticType && (targetNodeOption is null || node.Name == targetNodeOption)).ToArray();
                    if (string.IsNullOrWhiteSpace(targetNodeOption) || targetNodes.Length != 1 || targetNodes.Any(node =>
                        node.Selector.IdentityFields.Count != 1 || node.Selector.IdentityFields[0] != key.Field))
                    {
                        resolvedExecution = VerificationPartitionExecution.Global;
                        partitionFallbackReason = $"Grouping key '{key.Field}' is not the complete identity for exactly one explicitly scoped target node.";
                    }
                }
            }
            worksets.Add(new VerificationRuleWorkset(rule, definition.Type, rule.Scope,
                new DomainList<string>(semanticTypes.Order(StringComparer.Ordinal)),
                new DomainList<string>(sourceFields.Order(StringComparer.Ordinal)),
                new DomainList<string>(targetFields.Order(StringComparer.Ordinal)),
                OrderedKeys(groupingKeys), OrderedKeys(orderingKeys), OrderedKeys(lookupKeys),
                resolvedExecution, descriptor.PartitionKey, partitionKeyFields, partitionFallbackReason));
        }

        if (graph is not null)
            foreach (var node in graph.Nodes)
            {
                var roles = node.Type == MigrationNodeType.Source
                    ? new[] { VerificationArtifactRole.Source }
                    : new[] { VerificationArtifactRole.ExpectedTarget, VerificationArtifactRole.ActualTarget };
                foreach (var role in roles)
                    AddFields(fieldsByWorkset, new VerificationWorksetKey(role, node.SemanticType), node.Selector.IdentityFields);
            }

        return new VerificationExecutionPlan(worksets, fieldsByWorkset, keysByWorkset);
    }

    private static DomainList<VerificationOrderingKey> OrderedKeys(IEnumerable<VerificationOrderingKey> keys) =>
        new(keys.Distinct().OrderBy(key => key.SemanticType, StringComparer.Ordinal)
            .ThenBy(key => key.Field, StringComparer.Ordinal).ThenBy(key => key.Role).ThenBy(key => key.Descending));

    public DomainList<string> GetRequiredFields(VerificationArtifactRole role, string semanticType)
    {
        var exact = _requiredFields.GetValueOrDefault(new VerificationWorksetKey(role, semanticType));
        var wildcard = _requiredFields.GetValueOrDefault(new VerificationWorksetKey(role, "*"));
        return new DomainList<string>((exact is null ? Enumerable.Empty<string>() : exact)
            .Concat(wildcard is null ? Enumerable.Empty<string>() : wildcard)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    public DomainList<VerificationOrderingKey> GetRequiredKeys(VerificationArtifactRole role, string semanticType)
    {
        var exact = _requiredKeys.GetValueOrDefault(new VerificationWorksetKey(role, semanticType));
        var wildcard = _requiredKeys.GetValueOrDefault(new VerificationWorksetKey(role, "*"));
        return OrderedKeys((exact is null ? Enumerable.Empty<VerificationOrderingKey>() : exact)
            .Concat(wildcard is null ? Enumerable.Empty<VerificationOrderingKey>() : wildcard));
    }

    private static string ResolveSemanticType(VerificationRuleDefinition definition, RuleFieldRequirement requirement)
    {
        if (requirement.SemanticTypeOption is { } optionName &&
            definition.StructuredOptions.TryGetValue(optionName, out var value) && value is StringValue { Value.Length: > 0 } text)
            return text.Value;
        return requirement.DefaultSemanticType ?? "*";
    }

    private static string[] ReadFields(ValueNode value, VerificationRuleDefinition definition, string optionName) => value switch
    {
        StringValue { Value.Length: > 0 } text => [text.Value],
        StringValue => [],
        CollectionValue collection => collection.Values.Select(item => item is StringValue text ? text.Value :
            throw new VerificationRuleException("PSRULE008", $"Rule '{definition.Id.Value}' field option '{optionName}' contains a non-text value.")).ToArray(),
        _ => throw new VerificationRuleException("PSRULE008", $"Rule '{definition.Id.Value}' field option '{optionName}' is not a field reference.")
    };

    private static string[] GetMappedTargetFields(MigrationGraph? graph, VerificationRuleDefinition definition,
        RuleFieldRequirement requirement)
    {
        if (graph is null)
            throw new VerificationRuleException("PSRULE008",
                $"Rule '{definition.Id.Value}' needs graph-mapped target fields to build its workset.");
        var targetNode = requirement.TargetNodeOption is { } nodeOption &&
            definition.StructuredOptions.TryGetValue(nodeOption, out var nodeValue) && nodeValue is StringValue nodeText
            ? nodeText.Value : null;
        var semanticType = ResolveSemanticType(definition, requirement);
        var targetNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Target &&
            (targetNode is null || targetNode.Length == 0 || node.Name == targetNode) &&
            (semanticType == "*" || node.SemanticType == semanticType)).Select(node => node.Id).ToHashSet();
        return graph.Edges.Where(edge => edge.Targets.Any(target => targetNodes.Contains(target)))
            .SelectMany(edge => edge.Operation.Fields).Select(field => field.Target)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddFields(Dictionary<VerificationWorksetKey, HashSet<string>> fields,
        VerificationWorksetKey key, IEnumerable<string> values)
    {
        if (!fields.TryGetValue(key, out var set)) fields.Add(key, set = new HashSet<string>(StringComparer.Ordinal));
        set.UnionWith(values);
    }

    private static void AddKeys(Dictionary<VerificationWorksetKey, HashSet<VerificationOrderingKey>> keys,
        VerificationWorksetKey key, IEnumerable<VerificationOrderingKey> values)
    {
        if (!keys.TryGetValue(key, out var set)) keys.Add(key, set = []);
        set.UnionWith(values);
    }

    private static void AddDescriptorKeys(IEnumerable<VerificationOrderingKey> keys, VerificationOrderingRole role,
        HashSet<string> fields, VerificationArtifactRole artifactRole,
        Dictionary<VerificationWorksetKey, HashSet<string>> fieldsByWorkset,
        Dictionary<VerificationWorksetKey, HashSet<VerificationOrderingKey>> keysByWorkset)
    {
        foreach (var key in keys)
        {
            fields.Add(key.Field);
            var worksetKey = new VerificationWorksetKey(artifactRole,
                string.IsNullOrWhiteSpace(key.SemanticType) ? "*" : key.SemanticType);
            AddFields(fieldsByWorkset, worksetKey, [key.Field]);
            AddKeys(keysByWorkset, worksetKey, [key with { Role = role }]);
        }
    }
}