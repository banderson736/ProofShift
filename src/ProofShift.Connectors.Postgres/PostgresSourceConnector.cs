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

    protected override string DiscoveryColumnsSql => """
        SELECT n.nspname,c.relname,CASE WHEN c.relkind IN ('v','m') THEN 'view' ELSE 'table' END,
               a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),NOT a.attnotnull,a.attnum::integer
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
        WHERE c.relkind IN ('r','p','v','m') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
        ORDER BY n.nspname,c.relname,a.attnum
        """;
    protected override string DiscoveryKeysSql => """
        SELECT n.nspname,t.relname,i.relname,x.indisprimary,a.attname
        FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class t ON t.oid=x.indrelid
        JOIN pg_catalog.pg_class i ON i.oid=x.indexrelid JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace
        JOIN LATERAL unnest(x.indkey) WITH ORDINALITY k(attnum,position) ON true
        JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
        WHERE x.indisunique AND x.indisvalid AND k.position<=x.indnkeyatts
          AND n.nspname NOT IN ('pg_catalog','information_schema')
        ORDER BY n.nspname,t.relname,i.relname,k.position
        """;
    protected override string DiscoveryRelationshipsSql => """
        SELECT n.nspname,t.relname,c.conname,a.attname,rn.nspname,r.relname,ra.attname
        FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class t ON t.oid=c.conrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=t.relnamespace JOIN pg_catalog.pg_class r ON r.oid=c.confrelid
        JOIN pg_catalog.pg_namespace rn ON rn.oid=r.relnamespace
        JOIN LATERAL unnest(c.conkey,c.confkey) WITH ORDINALITY k(source,target,position) ON true
        JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.source
        JOIN pg_catalog.pg_attribute ra ON ra.attrelid=r.oid AND ra.attnum=k.target
        WHERE c.contype='f' ORDER BY n.nspname,t.relname,c.conname,k.position
        """;

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    protected override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
