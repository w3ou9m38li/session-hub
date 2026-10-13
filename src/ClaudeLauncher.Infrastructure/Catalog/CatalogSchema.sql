-- This launcher's own catalog. It holds metadata and view preferences only:
-- no transcript bodies, no cookies, no tokens, no copies of Claude's own database.
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS sessions (
  backend TEXT NOT NULL,
  account_context TEXT NOT NULL,
  organization_context TEXT NOT NULL,
  native_id TEXT NOT NULL,
  cli_session_id TEXT,
  title TEXT NOT NULL,
  search_key TEXT NOT NULL,
  project_ref TEXT,
  -- Search key of project_ref (NFKC + case folded, like search_key), so a search finds a
  -- conversation by its working directory as well as by its title. Derived, never shown.
  project_key TEXT,
  native_open_ref TEXT,
  last_activity_utc TEXT,
  source_revision TEXT NOT NULL,
  -- Claude's own sidebar group, read from its local preference cache. NULL means
  -- "ungrouped or unknowable" and is never written back to Claude.
  sidebar_group TEXT,
  -- This launcher's own group. NULL = no preference (Claude's sidebar_group shows through),
  -- '' = explicitly ungrouped, anything else = the group name. A sync never overwrites it.
  local_group TEXT,
  title_is_placeholder INTEGER NOT NULL DEFAULT 0 CHECK(title_is_placeholder IN (0,1)),
  visibility TEXT NOT NULL CHECK(visibility IN ('Active','ArchivedView')),
  native_archive_state TEXT NOT NULL CHECK(native_archive_state IN ('Unsupported','Unknown','Active','Archived')),
  source_coverage TEXT NOT NULL CHECK(source_coverage IN ('Resolved','UnknownSource','Unreachable','Partial')),
  local_pinned INTEGER NOT NULL DEFAULT 0 CHECK(local_pinned IN (0,1)),
  preference_revision INTEGER NOT NULL DEFAULT 0,
  -- This launcher's own name and note for the conversation (NULL = none), and the search key
  -- of the two together. A sync never overwrites them; nothing is written back to the apps.
  local_title TEXT,
  local_note TEXT,
  local_search_key TEXT,
  -- On a terminal transcript that is the same conversation as a desktop session: the desktop
  -- row's native id. Such a row is folded into the desktop row, not listed on its own.
  merged_into TEXT,
  -- Size of the transcript file, in bytes, as last seen by a sync (NULL: no file on disk).
  transcript_bytes INTEGER,
  -- On a terminal transcript a desktop session has since moved on from (Claude started a new
  -- one to continue the conversation): that desktop row's native id. Still listed on its own.
  continued_in TEXT,
  -- On a terminal transcript Claude Code began with a copy of another session's history (a
  -- resume): that session's id, while it is on disk too. A copy nothing was said in after the
  -- resume is folded (merged_into) into the row that holds the original.
  resumed_from TEXT,
  -- When a complete read of the source no longer found this conversation (the app deleted or
  -- moved its record). The row is kept: only the user deletes rows. NULL while it is there.
  source_gone_utc TEXT,
  PRIMARY KEY(backend,account_context,organization_context,native_id)
);

-- Conversations the user deleted from this tool. A sync never lists them again, even while
-- the app still has them; nothing is ever deleted from the apps themselves.
CREATE TABLE IF NOT EXISTS deleted_sessions (
  backend TEXT NOT NULL,
  account_context TEXT NOT NULL,
  organization_context TEXT NOT NULL,
  native_id TEXT NOT NULL,
  deleted_utc TEXT NOT NULL,
  PRIMARY KEY(backend,account_context,organization_context,native_id)
);

-- Paging order: pinned first, then normalised activity time DESC, then the full key ASC.
-- Rows with no timestamp sort last via the sentinel rather than being dropped from the page.
DROP INDEX IF EXISTS sessions_page;
CREATE INDEX IF NOT EXISTS sessions_list
ON sessions(visibility, local_pinned DESC,
            COALESCE(last_activity_utc,'0001-01-01T00:00:00.0000000Z') DESC,
            backend, account_context, organization_context, native_id);

CREATE INDEX IF NOT EXISTS sessions_search ON sessions(search_key);

-- Single row. Bumped inside the same transaction as any catalog or preference write,
-- so a paging cursor can detect that it is reading a superseded snapshot.
CREATE TABLE IF NOT EXISTS catalog_version (
  singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
  revision INTEGER NOT NULL
);
INSERT OR IGNORE INTO catalog_version(singleton_id,revision) VALUES(1,0);

-- Journal for both local view operations and (later) guided native operations.
-- A native operation and this database are not one transaction, so the journal is
-- what lets a restart re-derive the truth instead of blindly replaying.
CREATE TABLE IF NOT EXISTS operations (
  operation_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  state TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  before_state_json TEXT NOT NULL,
  after_state_json TEXT,
  verified_utc TEXT,
  failure_code TEXT
);

-- Per-backend capability evidence, pinned to the app version it was observed against.
CREATE TABLE IF NOT EXISTS capabilities (
  backend TEXT NOT NULL,
  operation TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('Unknown','Verified','Unsupported','VersionMismatch')),
  observed_app_version TEXT NOT NULL,
  observed_utc TEXT NOT NULL,
  PRIMARY KEY(backend,operation)
);
