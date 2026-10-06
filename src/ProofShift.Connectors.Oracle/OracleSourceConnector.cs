using System.Data;
using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using Oracle.ManagedDataAccess.Types;

namespace ProofShift.Connectors.Oracle;

public sealed class OracleSourceConnector : RelationalSourceConnectorBase, IConnectorConfigurationSchemaProvider
{
    private static readonly ConnectorConfigurationSchema Schema = new("oracle", "table",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["connection"] = "string-or-reference", ["schema"] = "string" },
        ["connection"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "string", ["schema"] = "string", ["table"] = "string", ["columns"] = "string"
        }, new Dictionary<string, string>(StringComparer.Ordinal),
        new IReadOnlyCollection<string>[] { ["name"], ["schema", "table"] });

    public OracleSourceConnector() : base("oracle", "0.10.0") { }
    public ConnectorConfigurationSchema ConfigurationSchema => Schema;
    protected override DbConnection CreateConnection(string connectionString) => new OracleConnection(connectionString) { BindByName = true };
    protected override string DefaultSchema => "";
    protected override object ReadProviderValue(DbDataReader reader, int ordinal)
    {
        var type = reader.GetDataTypeName(ordinal);
        if (type.Equals("TIMESTAMP WITH TIME ZONE", StringComparison.OrdinalIgnoreCase))
        {
            var timestamp = ((OracleDataReader)reader).GetOracleTimeStampTZ(ordinal);
            return new DateTimeOffset(DateTime.SpecifyKind(timestamp.Value, DateTimeKind.Unspecified), timestamp.GetTimeZoneOffset());
        }
        if (type.Equals("CLOB", StringComparison.OrdinalIgnoreCase) || type.Equals("NCLOB", StringComparison.OrdinalIgnoreCase))
            return ((OracleDataReader)reader).GetOracleClob(ordinal).Value;
        if (type.Equals("NUMBER", StringComparison.OrdinalIgnoreCase) || type.Equals("FLOAT", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var exact = ((OracleDataReader)reader).GetOracleDecimal(ordinal);
                var value = exact.Value;
                if (exact != new OracleDecimal(value))
                    throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType, "Oracle numeric precision exceeds the supported exact decimal model.");
                return exact.IsInt && value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;
            }
            catch (ConnectorReadException) { throw; }
            catch (Exception exception) when (exception is OverflowException or InvalidCastException or OracleException)
            {
                throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType, "Oracle numeric value cannot be represented without precision loss.");
            }
        }
        return base.ReadProviderValue(reader, ordinal);
    }

    protected override bool IsBinaryField(DbDataReader reader, int ordinal) =>
        reader.GetDataTypeName(ordinal).Equals("BLOB", StringComparison.OrdinalIgnoreCase) || base.IsBinaryField(reader, ordinal);

    protected override string ParameterName(string name) => name == "table" ? "object_name" : name;
    protected override string ParameterPlaceholder(string name) => $":{name}";
    protected override string InspectionFailureDiagnostic(Exception exception) =>
        exception is OracleException oracleException ? $"Oracle error {oracleException.Number}" : exception.GetType().Name;

    protected override ValueNode NormalizeProviderValue(object value, string providerType)
    {
        if (value is DateTime local && (providerType.Equals("DATE", StringComparison.OrdinalIgnoreCase) ||
            providerType.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)))
            return new LocalDateTimeValue(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
        return base.NormalizeProviderValue(value, providerType);
    }
    protected override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    protected override string ColumnsSql => """
        SELECT column_name,data_type,nullable,column_id FROM all_tab_columns
        WHERE owner=:schema AND table_name=:object_name ORDER BY column_id
        """;
    protected override string PrimaryKeySql => """
        SELECT cc.column_name FROM all_constraints c JOIN all_cons_columns cc
        ON cc.owner=c.owner AND cc.constraint_name=c.constraint_name
        WHERE c.constraint_type='P' AND c.owner=:schema AND c.table_name=:object_name ORDER BY cc.position
        """;
    protected override string ApproximateRowCountSql => """
        SELECT num_rows FROM all_tables WHERE owner=:schema AND table_name=:object_name
        """;
    protected override string DiscoveryColumnsSql => """
        SELECT c.owner,c.table_name,CASE WHEN v.view_name IS NULL THEN 'table' ELSE 'view' END,
               c.column_name,c.data_type,CASE WHEN c.nullable='Y' THEN 1 ELSE 0 END,c.column_id
        FROM all_tab_columns c LEFT JOIN all_views v ON v.owner=c.owner AND v.view_name=c.table_name
        WHERE c.owner=COALESCE(:schema,SYS_CONTEXT('USERENV','CURRENT_SCHEMA'))
        ORDER BY c.owner,c.table_name,c.column_id
        """;
    protected override string DiscoveryKeysSql => """
        SELECT i.table_owner,i.table_name,i.index_name,CASE WHEN c.constraint_type='P' THEN 1 ELSE 0 END,k.column_name
        FROM all_indexes i JOIN all_ind_columns k ON k.index_owner=i.owner AND k.index_name=i.index_name
        LEFT JOIN all_constraints c ON c.owner=i.table_owner AND c.index_name=i.index_name AND c.constraint_type='P'
        WHERE i.uniqueness='UNIQUE' AND i.table_owner=COALESCE(:schema,SYS_CONTEXT('USERENV','CURRENT_SCHEMA'))
        ORDER BY i.table_owner,i.table_name,i.index_name,k.column_position
        """;
    protected override string DiscoveryRelationshipsSql => """
        SELECT c.owner,c.table_name,c.constraint_name,f.column_name,r.owner,r.table_name,t.column_name
        FROM all_constraints c JOIN all_cons_columns f ON f.owner=c.owner AND f.constraint_name=c.constraint_name
        JOIN all_constraints r ON r.owner=c.r_owner AND r.constraint_name=c.r_constraint_name
        JOIN all_cons_columns t ON t.owner=r.owner AND t.constraint_name=r.constraint_name AND t.position=f.position
        WHERE c.constraint_type='R' AND c.owner=COALESCE(:schema,SYS_CONTEXT('USERENV','CURRENT_SCHEMA'))
        ORDER BY c.owner,c.table_name,c.constraint_name,f.position
        """;

    protected override void ConfigureDiscoveryCommand(DbCommand command, ConnectorContext context)
    {
        string? schema = context.Configuration.TryGet("schema", out var setting) ? setting.UseValue(value => value) : null;
        if (schema is not null && (schema.Length == 0 || !schema.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$' or '#')))
            throw new ConnectorConfigurationException(ConnectorIssueCodes.InvalidPhysicalIdentifier, "Oracle discovery schema is invalid.");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "schema";
        parameter.DbType = DbType.String;
        parameter.Value = (object?)schema ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}