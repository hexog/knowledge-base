# Agent gateway MVP

The gateway is a separate loopback-only HTTP process. It reads the same file database as `kb`; do not give an agent direct filesystem access to the database, server state, or client registry.

## Prepare and start

```sh
kb migrate --to 2
kb migrate --to 3
KB_DATABASE_PATH=/path/to/knowledge-base dotnet run --project src/KnowledgeBase.Server
```

The listener binds to `127.0.0.1:8765`. Set `KB_SERVER_PORT` to choose another loopback port. `KB_SERVER_STATE_PATH` selects the cursor key, audit log, and idempotency state directory. `KB_CLIENT_REGISTRY_PATH` selects the credential registry; both paths should be outside the portable database and protected by the owner's filesystem permissions.

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

The external profile receives only current reviewed records whose effective access is `external_allowed`; each cited source must also be explicitly external allowed. The local profile can inspect valid records in any status. Draft submissions always create drafts and never invoke the local extractor or accept records.

Classification changes take effect on subsequent requests. They cannot retract content already delivered to a client. The audit log records operation metadata and resource IDs, not queries, document text, citations, or tokens. Audit write failures prevent successful responses.
