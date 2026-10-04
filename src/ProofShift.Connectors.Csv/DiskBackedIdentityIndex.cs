using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Connectors.Csv;

internal sealed class DiskBackedIdentityIndex : IAsyncDisposable
{
    private const string FilePrefix = "proofshift-identities-";
    private static readonly TimeSpan AbandonedFileRetention = TimeSpan.FromHours(24);
    private readonly string _path;
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqliteCommand _insert;
    private bool _disposed;

    private DiskBackedIdentityIndex(
        string path,
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCommand insert)
    {
        _path = path;
        _connection = connection;
        _transaction = transaction;
        _insert = insert;
    }

    public static async Task<DiskBackedIdentityIndex> CreateAsync(CancellationToken cancellationToken)
    {
        SweepAbandonedFiles();
        var path = Path.Combine(Path.GetTempPath(), $"{FilePrefix}{Guid.NewGuid():N}.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE identities (identity_hash BLOB NOT NULL PRIMARY KEY) WITHOUT ROWID;";
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var transaction = connection.BeginTransaction();
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO identities (identity_hash) VALUES ($identity_hash);";
            insert.Transaction = transaction;
            var parameter = insert.CreateParameter();
            parameter.ParameterName = "$identity_hash";
            insert.Parameters.Add(parameter);
            await insert.PrepareAsync(cancellationToken).ConfigureAwait(false);
            return new DiskBackedIdentityIndex(path, connection, transaction, insert);
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            TryDelete(path);
            throw;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            TryDelete(path);
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Temporary identity index could not be created.");
        }
    }

    public async Task AddAsync(string identity, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        _insert.Parameters[0].Value = fingerprint;
        try
        {
            var inserted = await _insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted != 1)
            {
                throw new ConnectorReadException(ConnectorIssueCodes.DuplicateArtifactIdentity,
                    "CSV source contains duplicate artifact identities.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConnectorReadException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed,
                "Temporary identity index update failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try
            {
                await _insert.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            DeleteScratchFiles(_path);
        }
    }

    private static void SweepAbandonedFiles()
    {
        var temporaryDirectory = Path.GetTempPath();
        var cutoff = DateTime.UtcNow - AbandonedFileRetention;
        try
        {
            foreach (var path in Directory.EnumerateFiles(temporaryDirectory, $"{FilePrefix}*.sqlite*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        var extensionIndex = path.IndexOf(".sqlite", StringComparison.OrdinalIgnoreCase);
                        if (extensionIndex >= 0)
                        {
                            DeleteScratchFiles(path[..(extensionIndex + ".sqlite".Length)]);
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static void DeleteScratchFiles(string databasePath)
    {
        TryDelete(databasePath);
        TryDelete($"{databasePath}-journal");
        TryDelete($"{databasePath}-wal");
        TryDelete($"{databasePath}-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
