using System.Globalization;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ProofShift.Configuration;

internal static class CanonicalYamlSerializer
{
    public const string FormatVersion = "proofshift-config-canonical-v1";

    public static string Serialize(YamlNode node) => node switch
    {
        YamlMappingNode mapping => SerializeMapping(mapping),
        YamlSequenceNode sequence => $"[{string.Join(',', sequence.Children.Select(Serialize))}]",
        YamlScalarNode scalar => SerializeScalar(scalar),
        _ => throw new InvalidDataException("Unsupported YAML node type.")
    };

    private static string SerializeMapping(YamlMappingNode mapping)
    {
        var entries = mapping.Children
            .Select(pair => (Key: Serialize(pair.Key), Value: Serialize(pair.Value)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}:{pair.Value}");
        return $"{{{string.Join(',', entries)}}}";
    }

    private static string SerializeScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style == ScalarStyle.Plain)
        {
            var normalized = value.ToLowerInvariant();
            if (normalized is "null" or "~")
            {
                return "null";
            }

            if (normalized is "true" or "false")
            {
                return $"boolean:{normalized}";
            }

            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return $"number:{value}";
            }
        }

        return $"string:{JsonSerializer.Serialize(value)}";
    }
}
