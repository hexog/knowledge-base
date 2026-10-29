# Agent gateway MVP

The gateway is a separate loopback-only HTTP process. It reads the same file database as `kb`; do not give an agent direct filesystem access to the database, server state, or client registry. Run only one server process for a given server state directory. A process lock rejects a second server using that state. Do not point multiple state directories at one database.

## Prepare and start

```sh
kb migrate --to 2
kb migrate --to 3
KB_DATABASE_PATH=/path/to/knowledge-base dotnet run --project src/KnowledgeBase.Server
```

The listener binds to `127.0.0.1:8765`. Set `KB_SERVER_PORT` to choose another loopback port. `KB_SERVER_STATE_PATH` selects the cursor key, audit log, and idempotency state directory. `KB_CLIENT_REGISTRY_PATH` selects the credential registry; both paths should be outside the portable database and protected by the owner's filesystem permissions.

Run `kb client create` and `kb client revoke` with the same `KB_CLIENT_REGISTRY_PATH` as the server. If it is unset, both use `clients.json` next to `KB_SERVER_STATE_PATH` (by default `~/.kb/clients.json`). If the server uses a custom state directory, older CLI builds may still use the default registry; update the CLI or set `KB_CLIENT_REGISTRY_PATH` explicitly for both processes. Restart the server after changing its registry path. A token written to a different registry is rejected as `401 unauthorized` even if it was just created.

Back up the database, server state directory, and client registry together while the server is stopped. The state directory contains the cursor key, audit log, and durable draft-operation journal (`idempotency.json`). The registry contains client token hashes. These files are not part of the portable knowledge archive. Restoring only the database or only the server state can lose replay guarantees; keep the matching backup set.

## Credentials

```sh
kb client create --profile external --scopes search,read
kb client create --profile local --scopes search,read,draft
kb client revoke CLIENT_ID
```

The create command prints a random bearer token once. The registry stores only its SHA-256 hash. Never put the token in command-line arguments or logs. Profile is fixed by the registry and cannot be selected by an API request.

## API

- `POST /v1/search` accepts `{ "query": "...", "limit": 10, "cursor": null }`.
- `GET /v1/records/{uuid}` returns one allowed record.
- `GET /v1/sources/{uuid}/text?start_line=1&end_line=10` returns a bounded text range.
- `POST /v1/drafts` accepts a title, body, and exact source line citations. Include an `Idempotency-Key` header.

All calls require `Authorization: Bearer TOKEN`. The applicable scope is `search`, `read`, or `draft`. Responses use `Cache-Control: no-store`; requests with an `Origin` header are rejected. Missing and inaccessible resources share the same `404 resource_unavailable` response.

The external profile receives only current reviewed records whose effective access is `external_allowed`; each cited source must also be explicitly external allowed. The local profile can inspect valid records in any status, including `review_required`. Only the owner can accept a `review_required` record, and acceptance rechecks every source citation. Draft submissions always create drafts and never invoke the local extractor or accept records.

Draft creation requires an ASCII `Idempotency-Key` of 1–128 characters from letters, digits, `-`, `_`, `.`, or `:`. The first successful response is `201` and includes a stable record ID, operation ID, `status: draft`, and the policy revision at creation. Repeating the same client/key and normalized payload replays the same creation receipt with `Idempotency-Replayed: true`; it does not read the current record or change the receipt when the owner later accepts or archives that record. Reusing the key for a different payload returns `409 idempotency_conflict`. Source access is checked again on every replay. Reserved operations are recoverable after restart and keep their preallocated record ID; an ambiguous record collision requires owner recovery rather than creating a second draft.

The server accepts request bodies up to 1 MiB, allows up to 8 active HTTP requests plus a bounded waiting queue, and waits up to 5 seconds for a database operation lock. Slow request bodies are limited to 10 seconds. Queue overflow returns `429`; database lock timeout returns `503 retry_required`. These are server limits, not client-tunable request fields. Search cursors are random server session tokens, expire after 10 minutes, and are invalidated when the policy revision or client binding changes.

Classification changes take effect on subsequent requests. They cannot retract content already delivered to a client. The audit log records operation metadata and resource IDs, not queries, document text, citations, or tokens. Audit write failures prevent successful responses.
