using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Configuration;

namespace ProofShift.Recovery;

public sealed record RecoverySourceCheckpointRequirement
{
    public string SystemId { get; }
    public string EndpointId { get; }

    public RecoverySourceCheckpointRequirement(string systemId, string endpointId)
    {
        SystemId = Required(systemId, nameof(systemId));
        EndpointId = Required(endpointId, nameof(endpointId));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public static class RecoveryIssueCodes
{
    public const string MissingDefinition = "PSREC001";
    public const string MissingCapability = "PSREC002";
    public const string InvalidReverse = "PSREC003";
    public const string MissingCheckpoint = "PSREC004";
    public const string CorruptedCheckpoint = "PSREC005";
    public const string UnknownCompensator = "PSREC006";
    public const string IrreversibleProhibited = "PSREC007";
    public const string RehearsalFailed = "PSREC008";
    public const string ContextMismatch = "PSREC009";
    public const string ArtifactIntegrityFailure = "PSREC010";
}

public static class QualificationIssueCodes
{
    public const string VerificationIncomplete = "PSREADY001";
    public const string VerificationFailed = "PSREADY002";
    public const string SourceAccountingFailed = "PSREADY003";
    public const string TargetLineageFailed = "PSREADY004";
    public const string RecoveryPolicyFailed = "PSREADY005";
    public const string RehearsalFailed = "PSREADY006";
    public const string IrreversibleRiskProhibited = "PSREADY007";
    public const string ContextMismatch = "PSREADY008";
    public const string StaleTarget = "PSREADY009";
}

public sealed class RecoveryException(string code, string message) : Exception(message)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("Recovery issue code is required.", nameof(code))
        : code.Trim();
}

public sealed record EffectiveRecoveryPolicy
{
    public const string FingerprintVersion = RecoveryFingerprintVersions.Policy;

    public bool RequireRecoveryForDestructiveOperations { get; }
    public bool AllowIrreversibleOperations { get; }
    public bool RequireValidatedRestore { get; }
    public bool RequireRecoveryRehearsal { get; }
    public bool RequireIrreversibleApproval { get; }
    public long MaximumIrreversibleArtifacts { get; }
    public IReadOnlyCollection<RecoverySourceCheckpointRequirement> RequiredSourceCheckpoints { get; }
    public string Fingerprint { get; }

    public EffectiveRecoveryPolicy(bool requireRecoveryForDestructiveOperations = true,
        bool allowIrreversibleOperations = false, bool requireValidatedRestore = true,
        bool requireRecoveryRehearsal = true, long maximumIrreversibleArtifacts = 0,
        bool requireIrreversibleApproval = true,
        IEnumerable<RecoverySourceCheckpointRequirement>? requiredSourceCheckpoints = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumIrreversibleArtifacts);
        RequireRecoveryForDestructiveOperations = requireRecoveryForDestructiveOperations;
        AllowIrreversibleOperations = allowIrreversibleOperations;
        RequireValidatedRestore = requireValidatedRestore;
        RequireRecoveryRehearsal = requireRecoveryRehearsal;
        RequireIrreversibleApproval = requireIrreversibleApproval;
        MaximumIrreversibleArtifacts = maximumIrreversibleArtifacts;
        RequiredSourceCheckpoints = Array.AsReadOnly((requiredSourceCheckpoints ?? [])
            .OrderBy(item => item.SystemId, StringComparer.Ordinal).ThenBy(item => item.EndpointId, StringComparer.Ordinal).ToArray());
        Fingerprint = ComputeFingerprint();
    }

    private string ComputeFingerprint()
    {
        var parts = new[]
        {
            FingerprintVersion,
            RequireRecoveryForDestructiveOperations.ToString().ToLowerInvariant(),
            AllowIrreversibleOperations.ToString().ToLowerInvariant(),
            RequireValidatedRestore.ToString().ToLowerInvariant(),
            RequireRecoveryRehearsal.ToString().ToLowerInvariant(),
            MaximumIrreversibleArtifacts.ToString(CultureInfo.InvariantCulture),
            RequireIrreversibleApproval.ToString().ToLowerInvariant(),
            string.Join("\n", RequiredSourceCheckpoints.Select(item => $"{item.SystemId.Length}:{item.SystemId}{item.EndpointId.Length}:{item.EndpointId}"))
        };
        var canonical = string.Concat(parts.Select(part => $"{Encoding.UTF8.GetByteCount(part).ToString(CultureInfo.InvariantCulture)}:{part};"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public static class RecoveryPolicyConfigurationLoader
{
    public static EffectiveRecoveryPolicy Load(LoadedProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var file = configuration.ReferencedFiles.FirstOrDefault(item => item.Kind == ConfigurationFileKind.RecoveryPolicy)
            ?? throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Configuration does not reference a recovery policy file.");
        if (file.Document is not ConfigurationMappingNode root)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery policy must be a YAML mapping.");

        var requireDestructive = Boolean(root, "requireRecoveryForDestructiveOperations", defaultValue: true);
        var allowIrreversible = ReadIrreversibleAllowance(root);
        var requireRestoreValidation = Boolean(root, "requireValidatedRestore", defaultValue: true);
        var requireRehearsal = Boolean(root, "requireRecoveryRehearsal", defaultValue: true);
        var maximumIrreversible = Integer(root, "maximumIrreversibleArtifacts", defaultValue: 0);
        var requiredSources = ReadSourceCheckpointRequirements(root);
        var requireIrreversibleApproval = ReadIrreversibleApprovalRequirement(root);
        var recognized = new HashSet<string>(StringComparer.Ordinal)
        {
            "requireRecoveryForDestructiveOperations", "allowIrreversible", "requireValidatedRestore",
            "requireRecoveryRehearsal", "maximumIrreversibleArtifacts", "requiredSnapshots", "approval"
        };
        var unknown = root.Values.Keys.FirstOrDefault(key => !recognized.Contains(key));
        if (unknown is not null)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, $"Recovery policy contains unsupported key '{unknown}'.");

        return new EffectiveRecoveryPolicy(requireDestructive, allowIrreversible, requireRestoreValidation,
            requireRehearsal, maximumIrreversible, requireIrreversibleApproval, requiredSources);
    }

    private static bool ReadIrreversibleAllowance(ConfigurationMappingNode root)
    {
        if (!root.Values.TryGetValue("allowIrreversible", out var node)) return false;
        if (node is ConfigurationScalarNode scalar) return ParseBoolean(scalar, "allowIrreversible");
        if (node is ConfigurationMappingNode mapping)
        {
            if (mapping.Values.Keys.Any(key => key != "default"))
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "allowIrreversible currently supports only the 'default' policy key.");
            return mapping.Values.TryGetValue("default", out var defaultNode)
                ? ParseBoolean(defaultNode as ConfigurationScalarNode, "allowIrreversible.default")
                : false;
        }
        throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "allowIrreversible must be a boolean or a mapping containing default.");
    }

    private static bool Boolean(ConfigurationMappingNode root, string key, bool defaultValue) =>
        root.Values.TryGetValue(key, out var node) ? ParseBoolean(node as ConfigurationScalarNode, key) : defaultValue;

    private static List<RecoverySourceCheckpointRequirement> ReadSourceCheckpointRequirements(
        ConfigurationMappingNode root)
    {
        if (!root.Values.TryGetValue("requiredSnapshots", out var node)) return [];
        if (node is not ConfigurationSequenceNode sequence)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "requiredSnapshots must be a sequence of source system/storage mappings.");
        var requirements = new List<RecoverySourceCheckpointRequirement>();
        foreach (var entry in sequence.Values)
        {
            if (entry is not ConfigurationMappingNode mapping)
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Each requiredSnapshots entry must be a mapping.");
            if (mapping.Values.Keys.Any(key => key is not ("system" or "storage")))
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "requiredSnapshots entries support only system and storage keys.");
            var system = Scalar(mapping, "system", required: true);
            var endpoint = Scalar(mapping, "storage", required: true);
            requirements.Add(new RecoverySourceCheckpointRequirement(system!, endpoint!));
        }
        if (requirements.Distinct().Count() != requirements.Count)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "requiredSnapshots contains duplicate source endpoints.");
        return requirements;
    }

    private static bool ReadIrreversibleApprovalRequirement(ConfigurationMappingNode root)
    {
        if (!root.Values.TryGetValue("approval", out var node)) return true;
        if (node is not ConfigurationMappingNode approval ||
            !approval.Values.TryGetValue("irreversibleTransformations", out var irreversibleNode) ||
            irreversibleNode is not ConfigurationMappingNode irreversible)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "approval.irreversibleTransformations must be a mapping.");
        if (approval.Values.Keys.Any(key => key != "irreversibleTransformations") ||
            irreversible.Values.Keys.Any(key => key != "required"))
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery approval contains an unsupported policy key.");
        return Boolean(irreversible, "required", defaultValue: true);
    }

    private static string? Scalar(ConfigurationMappingNode root, string key, bool required)
    {
        if (root.Values.TryGetValue(key, out var node) && node is ConfigurationScalarNode { Kind: ConfigurationScalarKind.Text, Value: not null } scalar)
            return scalar.Value.Trim();
        if (required) throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, $"Recovery policy requires scalar '{key}'.");
        return null;
    }

    private static bool ParseBoolean(ConfigurationScalarNode? scalar, string path)
    {
        if (scalar?.Kind == ConfigurationScalarKind.Boolean && bool.TryParse(scalar.Value, out var value)) return value;
        throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, $"Recovery policy value '{path}' must be a boolean.");
    }

    private static long Integer(ConfigurationMappingNode root, string key, long defaultValue)
    {
        if (!root.Values.TryGetValue(key, out var node)) return defaultValue;
        if (node is not ConfigurationScalarNode { Kind: ConfigurationScalarKind.Number, Value: not null } scalar ||
            !long.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, $"Recovery policy value '{key}' must be a non-negative integer.");
        return value;
    }
}
