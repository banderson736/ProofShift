using System.Data;
using System.Data.Common;
using IBM.Data.Db2;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Db2;

public sealed class Db2SourceConnector : RelationalSourceConnectorBase, IConnectorConfigurationSchemaProvider
{
    private static readonly ConnectorConfigurationSchema Schema = new("db2", "table",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["connection"] = "string-or-reference", ["schema"] = "string" },
        ["connection"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "string", ["schema"] = "string", ["table"] = "string", ["columns"] = "string"
        }, new Dictionary<string, string>(StringComparer.Ordinal),
        new IReadOnlyCollection<string>[] { ["name"], ["schema", "table"] });

    public Db2SourceConnector() : base("db2", "0.10.0") { }
    public ConnectorConfigurationSchema ConfigurationSchema => Schema;

    protected override DbConnection CreateConnection(string connectionString) => new DB2Connection(connectionString);
    protected override string DefaultSchema => "";
    protected override string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    protected override string ColumnsSql => """
        SELECT COLNAME,TYPENAME,NULLS,COLNO+1 FROM SYSCAT.COLUMNS
        WHERE TABSCHEMA=@schema AND TABNAME=@table ORDER BY COLNO
        """;
    protected override string PrimaryKeySql => """
        SELECT k.COLNAME FROM SYSCAT.TABCONST c JOIN SYSCAT.KEYCOLUSE k
        ON k.TABSCHEMA=c.TABSCHEMA AND k.TABNAME=c.TABNAME AND k.CONSTNAME=c.CONSTNAME
        WHERE c.TYPE='P' AND c.TABSCHEMA=@schema AND c.TABNAME=@table ORDER BY k.COLSEQ
        """;
    protected override string ApproximateRowCountSql => """
        SELECT CARD FROM SYSCAT.TABLES WHERE TABSCHEMA=@schema AND TABNAME=@table
        """;
    protected override string DiscoveryColumnsSql => """
        SELECT c.TABSCHEMA,c.TABNAME,CASE WHEN t.TYPE='V' THEN 'view' ELSE 'table' END,
               c.COLNAME,c.TYPENAME,c.NULLS,c.COLNO+1
        FROM SYSCAT.COLUMNS c JOIN SYSCAT.TABLES t ON t.TABSCHEMA=c.TABSCHEMA AND t.TABNAME=c.TABNAME
        WHERE c.TABSCHEMA=COALESCE(@schema,CURRENT SCHEMA)
        ORDER BY c.TABSCHEMA,c.TABNAME,c.COLNO
        """;
    protected override string DiscoveryKeysSql => """
        SELECT i.TABSCHEMA,i.TABNAME,i.INDNAME,CASE WHEN i.UNIQUERULE='P' THEN 1 ELSE 0 END,k.COLNAME
        FROM SYSCAT.INDEXES i JOIN SYSCAT.INDEXCOLUSE k
        ON k.INDSCHEMA=i.INDSCHEMA AND k.INDNAME=i.INDNAME
        WHERE i.UNIQUERULE IN ('P','U') AND i.TABSCHEMA=COALESCE(@schema,CURRENT SCHEMA)
        ORDER BY i.TABSCHEMA,i.TABNAME,i.INDNAME,k.COLSEQ
        """;
    protected override string DiscoveryRelationshipsSql => """
        SELECT r.TABSCHEMA,r.TABNAME,r.CONSTNAME,f.COLNAME,r.REFTABSCHEMA,r.REFTABNAME,p.COLNAME
        FROM SYSCAT.REFERENCES r
        JOIN SYSCAT.KEYCOLUSE f ON f.TABSCHEMA=r.TABSCHEMA AND f.TABNAME=r.TABNAME AND f.CONSTNAME=r.CONSTNAME
        JOIN SYSCAT.KEYCOLUSE p ON p.TABSCHEMA=r.REFTABSCHEMA AND p.TABNAME=r.REFTABNAME AND p.CONSTNAME=r.REFKEYNAME AND p.COLSEQ=f.COLSEQ
        WHERE r.TABSCHEMA=COALESCE(@schema,CURRENT SCHEMA)
        ORDER BY r.TABSCHEMA,r.TABNAME,r.CONSTNAME,f.COLSEQ
        """;

    protected override void ConfigureDiscoveryCommand(DbCommand command, ConnectorContext context)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@schema";
        parameter.DbType = DbType.String;
        parameter.Value = context.Configuration.TryGet("schema", out var setting)
            ? setting.UseValue(value => value) : DBNull.Value;
        command.Parameters.Add(parameter);
    }
}