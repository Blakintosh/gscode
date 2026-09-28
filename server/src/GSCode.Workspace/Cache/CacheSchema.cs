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
    /// <remarks>
    /// 2: the <c>deps</c> table is gone. Nothing had written or read it since the same edges moved
    /// into the serialized record (<c>ScriptRecord.Dependencies</c>); the wipe a mismatch triggers
    /// drops it from a version-1 file.
    /// </remarks>
    public const int SchemaVersion = 2;

    /// <summary>Bumped when the ScriptRecord blob serialization changes.</summary>
    /// <remarks>
    /// Bump for ANY change to what a blob means, additive ones included, because an older blob still
    /// reads cleanly and describes the wrong thing. A field added without a bump deserializes as its
    /// default everywhere — a null OwnerClass reads as a plain function, a false FromMacro as text
    /// written in the file — and a ReferenceKind inserted mid-enum shifts every later ordinal on the
    /// wire, so each of an old blob's kinds reads as its neighbour.
    /// </remarks>
    public const int RecordFormatVersion = 8;

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
        """;
}
