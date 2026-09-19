using Microsoft.Data.Sqlite;
using QuotaArc.Providers;

namespace QuotaArc.Tests;

/// The bug this exists to stop: Cursor dropped `composerHeaders` from its
/// schema, so `Rows` started throwing `SqliteException: no such table` on
/// every real install — and nothing caught it. A multi-row read is polled on
/// a timer; it must degrade to "nothing found" instead of taking its caller
/// down with it, the same tolerance `Scalar` already has.
public class SqliteStoreTests
{
    [Fact]
    public void RowsReturnsEmptyWhenTheQueriedTableIsMissing()
    {
        var path = MakeStoreWithoutComposerHeaders();
        try
        {
            using var db = SqliteStore.Open(path);
            Assert.NotNull(db);
            var rows = SqliteStore.Rows(db!, "SELECT value FROM composerHeaders");
            Assert.Empty(rows);
        }
        finally
        {
            // Mirrors SqliteStore.Open's own Pooling=false: left pooled, the
            // native handle from this write connection can outlive its
            // Dispose() on Windows, and the delete below fails with the file
            // "used by another process" even though nothing still references it.
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { /* AV or a lingering handle; harmless in %TEMP% */ }
        }
    }

    /// Mirrors Cursor's actual post-migration schema: `ItemTable` and
    /// `cursorDiskKV` exist, `composerHeaders` does not.
    private static string MakeStoreWithoutComposerHeaders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sqlitestore-{Guid.NewGuid()}.sqlite");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        using var create = db.CreateCommand();
        create.CommandText = "CREATE TABLE ItemTable (key TEXT, value TEXT)";
        create.ExecuteNonQuery();
        return path;
    }
}
