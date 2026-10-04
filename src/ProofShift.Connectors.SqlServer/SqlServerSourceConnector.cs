using System.Data.Common;
using System.Data;
using Microsoft.Data.SqlClient;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Connectors.SqlServer;

public sealed class SqlServerSourceConnector : RelationalSourceConnectorBase
{
    public SqlServerSourceConnector() : base("sqlserver", "0.1.0")
    {
    }

    protected override IsolationLevel? CheckpointIsolationLevel => IsolationLevel.Serializable;

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
