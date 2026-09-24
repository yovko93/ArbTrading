using Arbitrage.LocalTransport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitrage.Infrastructure;

public sealed class DatabaseStartupException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public interface ISchemaBackup
{
    Task CreateAsync(TradingDbContext db, DateTimeOffset now, CancellationToken cancellationToken);
}

// Caller holds LocalRuntimeLease throughout backup and migration. BackupDatabase includes committed WAL pages.
public sealed class SqliteSchemaBackup : ISchemaBackup
{
    public const int RetentionCount = 5;
    public async Task CreateAsync(TradingDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var source = (SqliteConnection)db.Database.GetDbConnection();
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source.DataSource))!, "backups", "schema");
        try
        {
            ProtectedStorage.CreatePrivateDirectory(directory);
            var completed = Path.Combine(directory, $"schema-{now:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db");
            var file = completed + ".pending";
            ProtectedStorage.RejectLinks(source.DataSource);
            ProtectedStorage.RejectLinks(file);
            // Reserve without overwriting an existing file or following a link.
            using (new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            ProtectedStorage.RestrictFile(file);
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
                await using var check = target.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                if (!Equals(await check.ExecuteScalarAsync(cancellationToken), "ok")) throw new IOException();
            }
            finally { await db.Database.CloseConnectionAsync(); }
            ProtectedStorage.VerifyPrivateFile(file);
            File.Move(file, completed);
            foreach (var old in Directory.EnumerateFiles(directory, "schema-*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(RetentionCount))
            {
                ProtectedStorage.VerifyPrivateFile(old);
                File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        { throw new DatabaseStartupException("DatabaseBackupFailed"); }
    }
}
