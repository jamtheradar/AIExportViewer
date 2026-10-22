namespace ExportViewer.Core.Store;

internal static class Schema
{
    // Timestamps are stored as UTC text (yyyy-MM-ddTHH:mm:ss.ffffffZ) so they sort and compare as strings.
    // `raw` columns hold the original JSON so Expert view can show every field, including unknown ones.
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);

        CREATE TABLE IF NOT EXISTS source_files(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            collection TEXT,
            size INTEGER NOT NULL,
            mtime INTEGER NOT NULL,
            records INTEGER NOT NULL DEFAULT 0,
            skipped INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL,
            note TEXT,
            loaded_at TEXT);

        CREATE TABLE IF NOT EXISTS load_issues(
            id INTEGER PRIMARY KEY,
            path TEXT,
            severity INTEGER NOT NULL,
            message TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS exports(
            folder TEXT PRIMARY KEY,
            created_at TEXT,
            total_files INTEGER,
            raw TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS record_sources(
            entity TEXT NOT NULL,
            id TEXT NOT NULL,
            file_id INTEGER NOT NULL,
            PRIMARY KEY(entity, id, file_id)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS users(
            uuid TEXT PRIMARY KEY,
            full_name TEXT,
            email TEXT,
            phone TEXT,
            raw TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS logins(
            id INTEGER PRIMARY KEY,
            account_uuid TEXT,
            ts TEXT,
            ip TEXT,
            browser TEXT,
            os TEXT,
            device TEXT,
            method TEXT,
            raw TEXT NOT NULL,
            UNIQUE(account_uuid, ts, ip));
        CREATE INDEX IF NOT EXISTS ix_logins_account ON logins(account_uuid, ts);

        CREATE TABLE IF NOT EXISTS conversations(
            uuid TEXT PRIMARY KEY,
            name TEXT,
            summary TEXT,
            created_at TEXT,
            updated_at TEXT,
            account_uuid TEXT,
            raw TEXT NOT NULL,
            title TEXT,
            message_count INTEGER NOT NULL DEFAULT 0,
            total_messages INTEGER NOT NULL DEFAULT 0,
            first_message_at TEXT,
            last_message_at TEXT,
            traits INTEGER NOT NULL DEFAULT 0,
            fork_count INTEGER NOT NULL DEFAULT 0,
            integrity INTEGER NOT NULL DEFAULT 0,
            dirty INTEGER NOT NULL DEFAULT 1);
        CREATE INDEX IF NOT EXISTS ix_conv_account ON conversations(account_uuid, last_message_at);
        CREATE INDEX IF NOT EXISTS ix_conv_last ON conversations(last_message_at);
        CREATE INDEX IF NOT EXISTS ix_conv_dirty ON conversations(dirty);

        CREATE TABLE IF NOT EXISTS messages(
            uuid TEXT PRIMARY KEY,
            conversation_uuid TEXT NOT NULL,
            parent_uuid TEXT,
            sender TEXT,
            created_at TEXT,
            updated_at TEXT,
            seq INTEGER NOT NULL,
            traits INTEGER NOT NULL,
            preview TEXT,
            raw TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_messages_conv ON messages(conversation_uuid, seq);
        CREATE INDEX IF NOT EXISTS ix_messages_created ON messages(created_at);

        CREATE TABLE IF NOT EXISTS integrity_flags(
            id INTEGER PRIMARY KEY,
            conversation_uuid TEXT NOT NULL,
            severity INTEGER NOT NULL,
            code TEXT NOT NULL,
            detail TEXT NOT NULL,
            message_uuid TEXT);
        CREATE INDEX IF NOT EXISTS ix_flags_conv ON integrity_flags(conversation_uuid);

        CREATE TABLE IF NOT EXISTS projects(
            uuid TEXT PRIMARY KEY,
            name TEXT,
            description TEXT,
            is_private INTEGER,
            is_starter INTEGER,
            prompt_template TEXT,
            created_at TEXT,
            updated_at TEXT,
            creator_uuid TEXT,
            creator_name TEXT,
            raw TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS project_docs(
            uuid TEXT PRIMARY KEY,
            project_uuid TEXT NOT NULL,
            filename TEXT,
            content TEXT,
            created_at TEXT);
        CREATE INDEX IF NOT EXISTS ix_docs_project ON project_docs(project_uuid);

        CREATE TABLE IF NOT EXISTS memory_snapshots(
            id INTEGER PRIMARY KEY,
            account_uuid TEXT NOT NULL,
            file_id INTEGER NOT NULL,
            snapshot_at TEXT,
            raw TEXT NOT NULL,
            UNIQUE(account_uuid, file_id));

        CREATE TABLE IF NOT EXISTS artifacts(
            id TEXT PRIMARY KEY,
            kind TEXT,
            visibility TEXT,
            owner_uuid TEXT,
            updated_at TEXT,
            active_version TEXT,
            title TEXT,
            raw TEXT NOT NULL,
            folder TEXT NOT NULL,
            page_markdown TEXT,
            comments_raw TEXT,
            artifact_comments_raw TEXT);

        CREATE TABLE IF NOT EXISTS artifact_versions(
            artifact_id TEXT NOT NULL,
            version_id TEXT NOT NULL,
            title TEXT,
            created_at TEXT,
            description TEXT,
            content_folder TEXT,
            PRIMARY KEY(artifact_id, version_id)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS generic_records(
            id INTEGER PRIMARY KEY,
            collection TEXT NOT NULL,
            record_key TEXT,
            label TEXT,
            raw TEXT NOT NULL,
            UNIQUE(collection, record_key));

        CREATE TABLE IF NOT EXISTS search_docs(
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            ref_id TEXT NOT NULL,
            conversation_uuid TEXT,
            account_uuid TEXT,
            ts TEXT);
        CREATE INDEX IF NOT EXISTS ix_search_ref ON search_docs(ref_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
            title, body,
            tokenize = 'unicode61 remove_diacritics 2',
            prefix = '2 3');
        """;
}
