using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class PensionSchemaIntegrationTests
{
    private const string Password = "Synthetic-PS09-Only-Password!2026";

    [Fact]
    public async Task LegacyAndStructurallyDifferentTargetSchemasExecuteInContainers()
    {
        var sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").WithPassword(Password).Build();
        await sqlContainer.StartAsync(TestContext.Current.CancellationToken);
        await using var sqlCleanup = sqlContainer;
        var postgresContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgresContainer.StartAsync(TestContext.Current.CancellationToken);
        await using var postgresCleanup = postgresContainer;

        var schemaRoot = Path.Combine(AppContext.BaseDirectory, "scenarios", "pension-modernization", "ps09", "database");
        var sql = await File.ReadAllTextAsync(Path.Combine(schemaRoot, "sqlserver-source.sql"), TestContext.Current.CancellationToken);
        var postgres = await File.ReadAllTextAsync(Path.Combine(schemaRoot, "postgres-shadow-template.sql"), TestContext.Current.CancellationToken);

        var sqlBuilder = new SqlConnectionStringBuilder(sqlContainer.GetConnectionString())
        {
            Encrypt = false,
            TrustServerCertificate = true
        };
        await using (var connection = new SqlConnection(sqlBuilder.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME IN ('MEMBER', 'EMPLOYMENT_HISTORY', 'CONTRIBUTION', 'SERVICE_CREDIT', 'BENEFICIARY', 'RETIREMENT_ELECTION', 'BENEFIT_PAYMENT', 'DOCUMENT_INDEX')";
            Assert.Equal(8, (int)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }

        await using (var connection = new NpgsqlConnection(postgresContainer.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = postgres;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'";
            Assert.Equal(8L, (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
    }
}
