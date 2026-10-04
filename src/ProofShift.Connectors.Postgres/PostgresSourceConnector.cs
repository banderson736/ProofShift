using System.Data.Common;
using System.Data;
using Npgsql;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Connectors.Postgres;

public sealed class PostgresSourceConnector : RelationalSourceConnectorBase
{
    public PostgresSourceConnector() : base("postgres", "0.1.0")
    {
    }

    protected override IsolationLevel? CheckpointIsolationLevel => IsolationLevel.RepeatableRead;

    protected override string ColumnsSql => """
        SELECT column_name, data_type, is_nullable, ordinal_position
        FROM information_schema.columns
        WHERE table_schema = @schema AND table_name = @table
        ORDER BY ordinal_position
        """;

    protected override string PrimaryKeySql => """
        SELECT key_columns.column_name
        FROM information_schema.table_constraints AS constraints
        JOIN information_schema.key_column_usage AS key_columns
          ON key_columns.constraint_catalog = constraints.constraint_catalog
         AND key_columns.constraint_schema = constraints.constraint_schema
         AND key_columns.constraint_name = constraints.constraint_name
        WHERE constraints.constraint_type = 'PRIMARY KEY'
          AND constraints.table_schema = @schema
          AND constraints.table_name = @table
        ORDER BY key_columns.ordinal_position
        """;

    protected override string ApproximateRowCountSql => """
        SELECT GREATEST(classes.reltuples, 0)::bigint
        FROM pg_class AS classes
        JOIN pg_namespace AS namespaces ON namespaces.oid = classes.relnamespace
        WHERE namespaces.nspname = @schema AND classes.relname = @table AND classes.relkind IN ('r', 'p')
        """;

    protected override string DefaultSchema => "public";

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    protected override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
