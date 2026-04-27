using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Persistence;

/// Idempotent SQLite schema upgrade for the PII feature. The codebase uses
/// `Database.EnsureCreatedAsync` (not migrations) so EF only creates missing
/// tables on a fresh DB. Existing databases that predate this feature need
/// `ALTER TABLE` for the new `proxy_rules` columns. The new `pii_packs` and
/// `pii_rules` tables are picked up by `EnsureCreatedAsync` on fresh DBs but
/// must be created explicitly when upgrading. Both operations are guarded by
/// PRAGMA introspection so this can run on every startup safely.
public static class PiiSchemaUpgrader
{
    public static async Task EnsureUpgradedAsync(AdaptiveApiDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn is not SqliteConnection)
            return; // non-Sqlite providers (when added) own their migration story

        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await EnsureColumnAsync(conn, "proxy_rules", "PiiPackSlugsJson", "TEXT", ct);
        await EnsureColumnAsync(conn, "proxy_rules", "PiiRuleIdsJson", "TEXT", ct);
        await EnsureColumnAsync(conn, "proxy_rules", "PiiDisabledDetectorsJson", "TEXT", ct);

        await EnsureTableAsync(conn, "pii_packs", """
            CREATE TABLE IF NOT EXISTS pii_packs (
                Id            TEXT NOT NULL PRIMARY KEY,
                Slug          TEXT NOT NULL,
                Name          TEXT NOT NULL,
                Description   TEXT NOT NULL,
                DetectorsJson TEXT NOT NULL DEFAULT '[]',
                IsBuiltin     INTEGER NOT NULL DEFAULT 1,
                Ordinal       INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_pii_packs_Slug ON pii_packs (Slug);
            """, ct);

        await EnsureTableAsync(conn, "pii_rules", """
            CREATE TABLE IF NOT EXISTS pii_rules (
                Id          TEXT NOT NULL PRIMARY KEY,
                TenantId    TEXT NOT NULL,
                Name        TEXT NOT NULL,
                Description TEXT,
                Pattern     TEXT NOT NULL,
                Replacement TEXT NOT NULL,
                FlagsJson   TEXT,
                Enabled     INTEGER NOT NULL DEFAULT 1,
                CreatedAt   TEXT NOT NULL,
                UpdatedAt   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_pii_rules_TenantId ON pii_rules (TenantId);
            """, ct);
    }

    private static async Task EnsureColumnAsync(System.Data.Common.DbConnection conn,
        string table, string column, string type, CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct)) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ColumnExistsAsync(System.Data.Common.DbConnection conn,
        string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // PRAGMA table_info returns: cid, name, type, notnull, dflt_value, pk
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task EnsureTableAsync(System.Data.Common.DbConnection conn,
        string _, string ddl, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
