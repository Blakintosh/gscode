namespace GSCode.Workspace.Cache;

/// <summary>
/// The SQLite schema and the two version gates. Bump SchemaVersion when the table shape
/// changes; bump RecordFormatVersion when the serialized ScriptRecord blob layout changes.
/// Either mismatch (or a server-build-identity mismatch) wipes the cache and re-indexes —
/// there are deliberately no migrations.
/// </summary>
public static class CacheSchema
{
    /// <summary>Bumped when the table DDL below changes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Bumped when the ScriptRecord blob serialization changes.</summary>
    public const int RecordFormatVersion = 2;

    // meta keys.
    public const string MetaSchemaVersion = "schema_version";
    public const string MetaRecordFormatVersion = "record_format_version";
    public const string MetaServerBuildIdentity = "server_build_identity";

    /// <summary>Creates the tables if absent. WAL + busy_timeout are set on the connection, not here.</summary>
    public const string CreateTables = """
        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS files (
            path         TEXT PRIMARY KEY,
            language     INTEGER NOT NULL,
            context_id   TEXT NOT NULL,
            relative     TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            analysed_at  INTEGER NOT NULL,
            record       BLOB NOT NULL
        );

        CREATE TABLE IF NOT EXISTS deps (
            path     TEXT NOT NULL,
            dep_path TEXT NOT NULL,
            is_insert INTEGER NOT NULL,
            PRIMARY KEY (path, dep_path)
        );

        CREATE INDEX IF NOT EXISTS ix_deps_dep ON deps(dep_path);
        """;
}
