# Claude organisation export: observed schema

Source: one real Team export produced 2026-10-21 (manifest `version: "1.0"`), inspected locally.
All example values below are **synthetic**. Never paste real export values into this file.

Counts in brackets are what the sample contained, to give a sense of shape and frequency, not limits.

## 1. Delivery format

The export arrives as a **manifest JSON** plus **one zip per category per part**:

| Zip name | Category | Contents (paths inside the zip) |
| --- | --- | --- |
| `light_metadata-000.zip` | `light_metadata` | `users.json`, `login_history.json` |
| `conversations-000.zip` | `conversations` | `conversations.json` |
| `projects-000.zip` | `projects` | `projects/<project-uuid>.json` (one file per project) |
| `memories-000.zip` | `memories` | `memories/<account-uuid>.json` (one file per user) |
| `frames-000.zip` | `frames` | `artifacts/<artifact-uuid>/...` (see section 7) |

- The `-000` suffix is the `part` number. Larger orgs presumably get `-001`, `-002`, … per category. A category split across parts must be merged.
- Inside a zip, some categories have an inner folder (`projects/`, `memories/`, `artifacts/`) and some do not (`conversations.json`, `users.json`).
- When a user unzips manually they may end up with `projects-000/projects/*.json` **or** `projects/*.json` (the sample had both styles). The loader must detect collections by **inner path and file shape**, not only by the outer folder name.
- There is **no** `metadata` folder (the prompt assumed one); it is called `light_metadata`.
- Zip entries have a 1980-01-01 timestamp (no real mtime); do not rely on file dates.

## 2. Manifest (`manifest-<org-uuid>-<n>-<hex>-<yyyy-mm-dd-hh-mm-ss>.json`)

```json
{
  "instructions": "Download each file using the export_url. Each link expires ...",
  "created_at": "2026-01-01T00:00:00.000000+00:00",
  "total_files": 5,
  "version": "1.0",
  "data_files": [
    {
      "batch_index": 0,
      "export_url": "https://claude.ai/export/<org-uuid>/<...>",
      "category": "light_metadata",
      "part": 0,
      "filename": "light_metadata-000.zip"
    }
  ]
}
```

- Download host observed: **`claude.ai`** only (path `/export/<org-uuid>/<8 digits>/<32 hex>`, no query string). Whether the URL works without a logged-in browser session is **untested**. Phase 3's in-app download depends on this.
- `instructions` says each link **expires 24 hours after `created_at`** and **may stop working once used**. The app uses `created_at` + 24 h as the expiry. Treat a link as a password while it is live: never log or store it.
- No sizes and no checksums are given for the zips, so a download can only be checked by opening the zip successfully.
- The org UUID appears in the file name and URL; no org display name is present.

## 3. Users: `users.json`

Top level: array (8 records).

| Field | Type | Notes |
| --- | --- | --- |
| `uuid` | string (UUID) | **Account ID.** Primary key for a user. |
| `full_name` | string | Display name (free text, may be a first name or a nickname). |
| `email_address` | string | |
| `verified_phone_number` | string \| null | E.164 format when present. Personal data. |

## 4. Sign-in history: `login_history.json`

Top level: object `{ "login_events": [...] }` (48 events).

| Field | Type | Notes |
| --- | --- | --- |
| `account_uuid` | string | → `users.uuid` |
| `timestamp` | ISO 8601 with offset | |
| `ip_address` | string | IPv4 or IPv6. Personal data. |
| `user_agent.browser_family` / `browser_version` / `os_family` / `os_version` / `device_family` | string (`os_version` nullable) | Parsed user agent. Saw `Chrome`, `Electron`, `Claude`, `Edge`, `Android`, … |
| `method` | string | Saw only `"sso"`. |
| `location_info` | null | Always null in the sample. Keep as raw JSON. |

Not mentioned in the build prompt. Proposed: a "Sign-ins" tab on the user page.

## 5. Conversations: `conversations.json`

Top level: **one JSON array** of conversation objects (247 conversations, 2,087 messages, 34 MB). This is the file that can reach hundreds of MB; it must be read one array element at a time.

### 5.1 Conversation

| Field | Type | Notes |
| --- | --- | --- |
| `uuid` | string | Primary key. |
| `name` | string | Title. **Empty for 22%** of conversations; UI needs a fallback (first human message). |
| `summary` | string | Claude-generated summary; empty for 66%. Can mention project IDs in free text. |
| `created_at`, `updated_at` | ISO 8601 `Z`, microseconds | `updated_at` also changes for non-message events (see 5.5). |
| `account.uuid` | string | **Owner** → `users.uuid`. The only field under `account`. |
| `chat_messages` | array | Always present; never empty in the sample. Stored in **chronological order** (true for all 247). |

**Not present** (the build prompt expected them): project link, model, starred flag, settings, current leaf/branch pointer. See section 9.

### 5.2 Message (`chat_messages[]`)

| Field | Type | Notes |
| --- | --- | --- |
| `uuid` | string | Primary key (UUIDv7-style, time ordered). |
| `parent_message_uuid` | string | → another message's `uuid` in the same conversation, **or** the root sentinel `00000000-0000-4000-8000-000000000000`. Present on every message. |
| `sender` | string | `"human"` \| `"assistant"`. First message is always `human`. |
| `created_at`, `updated_at` | ISO 8601 | |
| `text` | string | **Lossy flattened copy** of the message for old clients. See 5.4. |
| `content` | array of blocks | The real message. May be **empty** (see 5.5). |
| `attachments` | array | Pasted or uploaded text with its extracted contents (see 5.3). |
| `files` | array | References to uploaded or generated files, **without the file data** (see 5.3). |

No per-message model, stop reason, token usage or rating fields.

### 5.3 Attachments and files

`attachments[]` (56 total, all on human messages):

| Field | Type | Notes |
| --- | --- | --- |
| `file_name` | string | Usually empty (pasted text). |
| `file_size` | int | Bytes. |
| `file_type` | string | `"txt"`, MIME types such as `"application/xml"`. |
| `extracted_content` | string | Full text. **Up to ~5 MB in one string.** Searchable, often sensitive. |

`files[]` (344 total, on both human and assistant messages):

| Field | Type | Notes |
| --- | --- | --- |
| `file_uuid` | string | Also referenced from tool results (`content[].content[].file_uuid`). |
| `file_name` | string \| null | Can be empty or null. |

**The file data itself is not in the export**: images, PDFs and generated documents are references only. The UI should say "File not included in export".

### 5.4 `text` and `content` compared

- In 1,749 of 2,087 messages `text` is exactly the concatenation of the `text` blocks in `content`.
- In 338 it differs, but **only** because `text`:
  - replaces each non-text block (tool use, tool result, artifacts) with the placeholder
    `` ```\nThis block is not supported on your current device yet.\n``` ``
    (up to 50 per message), and
  - adds extra newlines between blocks.
- After removing placeholders and collapsing whitespace, **no message disagreed**.
- 66 messages have empty `text`: empty assistant replies, thinking-only replies, file-only human messages.
- **Implication:** viewers that render `text` show placeholders instead of the tool work, which is one likely cause of the "cut off" look. Always render from `content`. The integrity check must normalise as above before comparing, otherwise it flags 16% of messages for nothing.

### 5.5 Message tree, branching and "cut off" causes

- The tree is built from `parent_message_uuid`. Root children point at the sentinel UUID.
- **No pointer to the current branch is exported.** Rule: the "latest branch" is the path from the root to the **leaf with the latest `created_at`**.
- In the sample: 9 conversations have forks. 8 forks are at a human message (the user edited their message), 1 is at an assistant message (retry). 3 conversations have more than one root child (the first message was edited).
- No parent pointed at a missing message in the sample, but this must still be checked.
- **Empty assistant replies** (`content: []`, `text: ""`): 21 found.
  - 8 are the last message of their conversation, on the latest branch. The conversation really ends with no reply in the export.
  - 10 are mid-conversation on the latest branch, and the user carried on afterwards.
  - 3 are dead branches.
- **Thinking-only assistant replies** (a single `thinking` block, no text): 7 found, 1 of them at the end of a conversation.
- **Conversations with no content at all:** 8 found. Every message (human and assistant) has `content: []` and `text: ""`, though all timestamps and parent links are present, and sometimes a `files[]` reference. The conversation happened but none of its text was exported. These account for most of the empty messages above. The integrity check gives them a single "no content" flag.
- **Empty human messages** with no files or attachments: 13 found, nearly all inside those 8 conversations.
- **Last message earlier than `updated_at`:** 16 conversations, with gaps from a few seconds to 23 days (median gap across all conversations: 0 s). Renames, moves or starring probably also update `updated_at`, so treat this as a weak signal only.

### 5.6 Content block types (`content[].type`)

Common to every block: `type`, `start_timestamp`, `stop_timestamp` (ISO 8601), `flags` (always null).

| `type` | Count | Sender | Key fields |
| --- | --- | --- | --- |
| `text` | 2,166 | both | `text` (markdown), `citations[]` |
| `thinking` | 1,040 | assistant | `thinking` (**always `""`**; the text is not exported), `summaries[].summary` (short one-line summaries, the only visible reasoning), `cut_off`, `truncated`, `hidden`, `thinking_hidden` (always true), `signature`, `alternative_display_type` |
| `tool_use` | 1,292 | assistant | `id` (`toolu_…`), `name`, `input` (object, tool specific), `message` (status line such as "Searching the web"), `integration_name`, `integration_icon_url` (remote URL, do not load), `icon_name`, `display_content`, `is_mcp_app`, `mcp_server_url`, `tool_origin`, `approval_*`, `context`, `hidden_in_chat` |
| `tool_result` | 1,292 | assistant | `tool_use_id` → `tool_use.id` in the **same message**, `name`, `content[]` (see 5.7), `is_error`, `structured_content` (tool specific object), `display_content`, `meta`, `message`, `integration_name`, `icon_name` |
| `injected_prompt_block` | 85 | human | `prompt` (text the system added to the user's turn), `injection_source` (`memory_block_head`, `melange_tombstone`, `date_note`), `initial_turn_only`, `skip_on_truncated_continuation`. Show it collapsed, labelled "Added by Claude (memory/context)", **not** as user-typed text. |

Tool use and results run on Claude's side, so both sit inside the **assistant** message, in sequence. Tool names seen (35): `bash_tool`, `web_search`, `web_search_fast`, `web_fetch`, `view`, `create_file`, `str_replace`, `present_files`, `conversation_search`, `memory_read`, `image_search`, `places_search`, `Artifact`, `Claude Docs:*` (MCP), `visualize:*`, `*_display_v0` widgets, `ask_user_input_v0`, `message_compose_v1`, etc. New names will keep appearing, so render unknown tools generically (name, input JSON, result).

`text` block `citations[]`: `uuid`, `start_index`, `end_index` (character offsets into `text`), `details.type` (`web_search_citation`), `details.url`. A block may also have `citations_grouping_mode`.

### 5.7 Tool result content items (`tool_result.content[].type`)

| `type` | Count | Fields |
| --- | --- | --- |
| `text` | 816 | `text` (up to 120 KB, e.g. command output or JSON) |
| `knowledge` | 2,515 | Web search hit: `title`, `url`, `text`, `metadata` (`type: webpage_metadata`, `site_domain`, `site_name`, `favicon_url` remote), `is_missing`, `is_citable`, `prompt_context_metadata` (`search_provider`, `url`, `age`, `content_type`, `destination_url`, `final_url`, `mime_type`), `links` |
| `image` | 151 | `file_uuid` → `files[]`; image data not included |
| `local_resource` | 120 | Generated file: `file_path` (e.g. `/mnt/user-data/outputs/x.docx`), `name`, `mime_type`, `artifact_publishable` |
| `image_gallery` | 4 | `images[]` / `spare_images[]` with remote `url`, `thumbnail_url`, `page_url`, `title`, `source`, sizes, `telemetry`; `is_expired` |

### 5.8 `display_content` (on tool_use / tool_result)

What the Claude UI showed instead of raw JSON. `display_content.type` values:
`json_block` (711), `code_block` (311: `language`, `code`, `filename`), `text` (307: `text`), `table` (84: `table` as `string[][]`), `rich_link` (40: `link.{title,url,source,icon_url}`, `is_trusted`), `rich_content` (26: `content[]` with `title`, `subtitles[]`, …), `file` (7: `published_url`, `published_artifact_id`, `published_action`, `title`, `owned_by_account`).

`structured_content` and `meta` are tool-specific and open-ended: memory ops (`memory_op_kind`, `memory_id`, `path`), artifacts (`artifact_id` → frames `artifact.id`, `version` → frames version id, `files[]`, `url`), recipes, places (with remote photo URLs), docs. Show these as a JSON tree; no dedicated views in v1.

## 6. Projects: `projects/<uuid>.json`

One object per file (35 files).

| Field | Type | Notes |
| --- | --- | --- |
| `uuid` | string | Primary key. Matches the file name. |
| `name` | string | Can be empty; not unique. |
| `description` | string | Often empty. |
| `is_private` | bool | |
| `is_starter_project` | bool | e.g. Anthropic's "How to use Claude" project. |
| `prompt_template` | string | Project instructions. Usually empty. |
| `created_at`, `updated_at` | ISO 8601 | |
| `creator.uuid`, `creator.full_name` | string | → `users.uuid` |
| `docs[]` | array | Project knowledge: `uuid`, `filename` (`.docx`, `.odt`, `.md`, `.cs`, `.tsx`, `.pdf`, … or empty), `content` (**already extracted text**, up to 1.2 MB), `created_at` |

**No list of member conversations, and no members or permissions.**

## 7. Memories: `memories/<account-uuid>.json`

One object per user who has memory (4 of 8).

| Field | Type | Notes |
| --- | --- | --- |
| `account_uuid` | string | → `users.uuid` |
| `conversations_memory` | string | Markdown summary memory (up to ~8 KB). |
| `memory_files[]` | array | File-based memory: `path` (e.g. `/profile.md`, `/topics/<x>.md`, `/projects/<project-uuid>/overview.md`), `content` (markdown, often with YAML front matter), `updated_at` |
| `project_memories` | object, optional | Map of **project uuid → markdown string**. All keys matched a real project. |

## 8. Frames (artifacts and pages): `artifacts/<artifact-uuid>/`

24 artifacts, 1,356 files, 200 MB unzipped (mostly repeated app bundles).

### 8.1 `artifact.json`

| Field | Type | Notes |
| --- | --- | --- |
| `id` | string | Primary key. Referenced from conversations (`structured_content.artifact_id`, `display_content.published_artifact_id`). |
| `kind` | string | `"artifact"` (web app / deck / design) or `"page"` (Claude Docs document). |
| `visibility` | string | `private`, `organization`, `invited` |
| `owner_account` | string | → `users.uuid` |
| `updated_at` | ISO 8601 | |
| `active_version` | string | → `versions[].id` |
| `versions[]` | array | `id` (`<unix-seconds>-<hex4>`), `title`, `created_at`, `description` (optional) |
| `shared_with` | object, optional | `viewers`, `editors` (counts only) |

### 8.2 Folder contents by kind

- **`page`** (12): `page.md` holds the **current** document only, in markdown (tables, headings, HTML comments such as `<!-- tab: … -->`). `comments.json` is always present. Some have `artifact_comments.json`. **Older versions are listed in `artifact.json` but their content is not exported.**
- **`artifact` with content** (3 of 12): `versions/<version-id>/` per version plus `versions/<version-id>.files.json`:
  - `index.html`: a full HTML app with **inline JavaScript**. Untrusted; never run it.
  - `SKILL.md`, `artifact-type/` (`app.js` up to 4 MB, `app.css`, runtime JS, `reference/*.md`, `thumbnail/thumbnail.json`), `project/` (`deck.json` or `canvas.json`, slide/board `.html` files, `.md` notes).
- **`artifact` without content** (9 of 12): only `artifact.json`. **The content of these artifacts is missing from the export.** The UI should say so.

`<version>.files.json`: `{ "files": [ { "original_path", "exported_as", "sha256", "size_bytes", "mime_type" } ] }`. MIME types seen: `text/html`, `text/markdown`, `application/json`, `text/javascript`, `text/css`, `font/woff2`. The sha256 values can be used to check file integrity.

`comments.json`: array of threads `{ resolved, tab?, quoted_text?, comments: [ { body, author: { uuid, full_name }, posted_by_agent?, created_at } ] }`.
`artifact_comments.json`: `{ threads: [ { created_at, resolved, carried, comments: [ { author_index, author_role, author_is_artifact_owner, text, created_at, to_claude_at } ] } ] }`.

`project/deck.json` and `canvas.json` are versioned (`v`) layout documents (`title`, `order[]`, `sections`, `boards`, `notes`, `faces` with **remote font URLs**, `designSystems`). Show them as JSON. Do not interpret them in v1.

## 9. Entity links

```
users.uuid ─┬─< conversations.account.uuid
            ├─< login_history.login_events[].account_uuid
            ├─< projects.creator.uuid
            ├─< memories.account_uuid  (file name = account uuid)
            ├─< frames artifact.owner_account
            └─< frames comments[].author.uuid

conversation ─< chat_messages ─ parent_message_uuid → chat_messages.uuid | ROOT sentinel
message ─< content[] ; tool_result.tool_use_id → tool_use.id (same message)
message.files[].file_uuid ←─ tool_result.content[].file_uuid
tool_result.structured_content.artifact_id / display_content.published_artifact_id → artifact.id
structured_content.version → artifact.versions[].id
memories.project_memories{key} → projects.uuid
memories.memory_files[].path "/projects/<uuid>/…" → projects.uuid
```

**No reliable link between conversations and projects.** Project UUIDs appear only inside free text (memory paths in `injected_prompt_block.prompt`, `memory_read` tool inputs, summaries) in 49 conversations. That shows the user's *memory* about a project was loaded, not that the chat belongs to the project. The "project → conversations" view and the conversation project filter cannot be built faithfully from this export.

**No model information anywhere.** The model filter cannot be built.

## 10. Other observations

- Conversations in the sample span only 2026-09-20 to 2026-10-21, while projects date back to February. The export (or this org's retention) may cover a limited date range. Show the date range prominently.
- All timestamps are UTC (`Z` or `+00:00`) with up to microsecond precision. Frames comments use JavaScript-style `…Z` with milliseconds. Parse with `DateTimeOffset` and show in local time.
- Remote URLs appear throughout: favicons, integration icons, image galleries, place photos, font faces, citations. None may be loaded.
- Personal and sensitive data appears in structured fields (emails, phones, IPs) as well as in free text, attachments and project docs.
