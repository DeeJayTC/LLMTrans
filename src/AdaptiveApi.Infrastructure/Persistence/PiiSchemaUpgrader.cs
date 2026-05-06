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

        // DeepL Translation Memory binding (per-route).
        await EnsureColumnAsync(conn, "routes", "TranslationMemoryId", "TEXT", ct);
        await EnsureColumnAsync(conn, "routes", "TranslationMemoryThreshold", "INTEGER", ct);

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

        // plugin_settings table came in with the plugin SDK. Existing pre-plugin
        // databases need it created on upgrade (EnsureCreated only acts on
        // first-run empty databases). The Enabled column was added later still,
        // so an ALTER guards the in-between case.
        await EnsureTableAsync(conn, "plugin_settings", """
            CREATE TABLE IF NOT EXISTS plugin_settings (
                TenantId      TEXT NOT NULL,
                PluginId      TEXT NOT NULL,
                SettingsJson  TEXT NOT NULL,
                Enabled       INTEGER NOT NULL DEFAULT 1,
                UpdatedAt     TEXT NOT NULL,
                PRIMARY KEY (TenantId, PluginId)
            );
            """, ct);
        await EnsureColumnAsync(conn, "plugin_settings", "Enabled", "INTEGER NOT NULL DEFAULT 1", ct);

        // RouteProfile bundles glossary + styles + proxy rule into a single
        // named row a route can opt into. New table + new column on routes.
        await EnsureTableAsync(conn, "route_profiles", """
            CREATE TABLE IF NOT EXISTS route_profiles (
                Id                    TEXT NOT NULL PRIMARY KEY,
                TenantId              TEXT NOT NULL,
                Name                  TEXT NOT NULL,
                Description           TEXT,
                GlossaryId            TEXT,
                RequestStyleRuleId    TEXT,
                ResponseStyleRuleId   TEXT,
                ProxyRuleId           TEXT,
                CreatedAt             TEXT NOT NULL,
                UpdatedAt             TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_route_profiles_TenantId ON route_profiles (TenantId);
            """, ct);
        await EnsureColumnAsync(conn, "routes", "ProfileId", "TEXT", ct);

        // request_rules: tenant-managed regex rules that fire on every proxied
        // request without a C# plugin. Built-in hook reads enabled rows in
        // priority order; see AdaptiveApi.Core.RequestRules.
        await EnsureTableAsync(conn, "request_rules", """
            CREATE TABLE IF NOT EXISTS request_rules (
                Id              TEXT NOT NULL PRIMARY KEY,
                TenantId        TEXT NOT NULL,
                RouteId         TEXT,
                Name            TEXT NOT NULL,
                Description     TEXT,
                Scope           TEXT NOT NULL DEFAULT 'request-body',
                HeaderName      TEXT,
                Pattern         TEXT NOT NULL,
                FlagsJson       TEXT,
                Action          TEXT NOT NULL DEFAULT 'log',
                BlockStatus     INTEGER,
                ActionPayload   TEXT,
                ActionHeader    TEXT,
                Priority        INTEGER NOT NULL DEFAULT 0,
                Enabled         INTEGER NOT NULL DEFAULT 1,
                CreatedAt       TEXT NOT NULL,
                UpdatedAt       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_request_rules_TenantEnabled
                ON request_rules (TenantId, Enabled, Priority);
            CREATE INDEX IF NOT EXISTS IX_request_rules_RouteId
                ON request_rules (RouteId);
            """, ct);

        // secrets: at-rest-encrypted blobs, AES-GCM. Plaintext never lives
        // in the row. See AdaptiveApi.Infrastructure.Secrets for the crypto
        // framing.
        await EnsureTableAsync(conn, "secrets", """
            CREATE TABLE IF NOT EXISTS secrets (
                Key         TEXT NOT NULL PRIMARY KEY,
                Ciphertext  BLOB NOT NULL,
                Nonce       BLOB NOT NULL,
                Tag         BLOB NOT NULL,
                KeyVersion  INTEGER NOT NULL DEFAULT 1,
                UpdatedAt   TEXT NOT NULL
            );
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
