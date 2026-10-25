using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Data.Common;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Pension;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Reporting;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class VersionCommands
{
    internal static string GetProductVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        GetAssemblyMetadata("ProofShiftProductVersion");


    internal static string GetAssemblyMetadata(string key) =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value ?? "unknown";

}
