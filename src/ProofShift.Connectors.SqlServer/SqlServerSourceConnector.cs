using System.Data.Common;
using System.Data;
using Microsoft.Data.SqlClient;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.SqlServer;

public sealed class SqlServerSourceConnector : RelationalSourceConnectorBase
{
    public SqlServerSourceConnector() : base("sqlserver", "0.1.0")
    {
    }

    public override CheckpointConsistencyDecision ResolveCheckpointConsistency(ConnectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var requested = GetOptional(context, "checkpoint.consistency") ?? "observed";
        if (string.Equals(requested, "observed", StringComparison.OrdinalIgnoreCase))
        {
            if (GetOptional(context, "checkpoint.isolation") is not null)
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration,
                    "SQL Server checkpoint isolation requires checkpoint.consistency: transaction-consistent.");
            return new CheckpointConsistencyDecision("observed", "observed", SourceConsistencyGuarantee.Observed, null, null);
        }

        if (!string.Equals(requested, "transaction-consistent", StringComparison.OrdinalIgnoreCase))
            throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration,
                "SQL Server checkpoint.consistency must be observed or transaction-consistent.");

        var isolationName = GetOptional(context, "checkpoint.isolation")
            ?? throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration,
                "Transaction-consistent SQL Server checkpoints require an explicit checkpoint.isolation.");
        var allowDowngrade = GetOptional(context, "checkpoint.allowDowngrade");
        var downgradeAllowed = allowDowngrade is not null && bool.TryParse(allowDowngrade, out var parsedAllow) && parsedAllow;
        var isolation = isolationName.ToLowerInvariant() switch
        {
            "snapshot" => IsolationLevel.Snapshot,
            "serializable" => IsolationLevel.Serializable,
            "read-committed" when downgradeAllowed => IsolationLevel.ReadCommitted,
            "read-committed" => throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration,
                "Read-committed does not provide a transaction-consistent checkpoint guarantee; explicitly allow downgrade to use it."),
            _ => throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration,
                "SQL Server checkpoint.isolation must be snapshot, serializable, or explicitly downgraded read-committed.")
        };
        var guarantee = isolation == IsolationLevel.ReadCommitted
            ? SourceConsistencyGuarantee.Observed : SourceConsistencyGuarantee.Consistent;
        var downgrade = guarantee == SourceConsistencyGuarantee.Observed
            ? "Requested transaction-consistent capture was explicitly downgraded to observed using read-committed."
            : null;
        return new CheckpointConsistencyDecision("transaction-consistent", NormalizeIsolationName(isolation),
            guarantee, downgrade, isolation);
    }

    private static string? GetOptional(ConnectorContext context, string key) =>
        context.Configuration.TryGet(key, out var setting) ? setting.UseValue(value => value.Trim()) : null;

    protected override string ColumnsSql => """
        SELECT columns.name, types.name, columns.is_nullable, columns.column_id
        FROM sys.tables AS tables
        JOIN sys.schemas AS schemas ON schemas.schema_id = tables.schema_id
        JOIN sys.columns AS columns ON columns.object_id = tables.object_id
        JOIN sys.types AS types ON types.user_type_id = columns.user_type_id
        WHERE schemas.name = @schema AND tables.name = @table
        ORDER BY columns.column_id
        """;

    protected override string PrimaryKeySql => """
        SELECT columns.name
        FROM sys.tables AS tables
        JOIN sys.schemas AS schemas ON schemas.schema_id = tables.schema_id
        JOIN sys.indexes AS indexes ON indexes.object_id = tables.object_id AND indexes.is_primary_key = 1
        JOIN sys.index_columns AS index_columns ON index_columns.object_id = indexes.object_id AND index_columns.index_id = indexes.index_id
        JOIN sys.columns AS columns ON columns.object_id = index_columns.object_id AND columns.column_id = index_columns.column_id
        WHERE schemas.name = @schema AND tables.name = @table
        ORDER BY index_columns.key_ordinal
        """;

    protected override string ApproximateRowCountSql => """
        SELECT SUM(partitions.row_count)
        FROM sys.dm_db_partition_stats AS partitions
        JOIN sys.tables AS tables ON tables.object_id = partitions.object_id
        JOIN sys.schemas AS schemas ON schemas.schema_id = tables.schema_id
        WHERE schemas.name = @schema AND tables.name = @table AND partitions.index_id IN (0, 1)
        """;

    protected override string DefaultSchema => "dbo";

    protected override DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    protected override string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
