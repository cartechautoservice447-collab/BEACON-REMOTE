<!-- LOVABLE:BEGIN -->
> [!IMPORTANT]
> This project is connected to [Lovable](https://lovable.dev). Avoid rewriting
> published git history — force pushing, or rebasing/amending/squashing commits
> that are already pushed — as it rewrites history on Lovable's side and the
> user will likely lose their project history.
>
> Commits you push to the connected branch sync back to Lovable and show up in
> the editor, so keep the branch in a working state.

## What Lovable manages

Rules outside this block are the project's own. Change the pieces below in
Lovable, not by hand. With the [Lovable MCP](https://mcp.lovable.dev)
connected, `send_message` asks Lovable to do it; otherwise ask the user to
use the editor.

- **Database.** Do not add or edit `supabase/migrations/` or `drizzle/`.
  Ask Lovable to change the schema.
- **Generated files.** Do not edit a file whose header says it is automatically
  generated.
- **AI gateway.** Keep model calls on Lovable's gateway, and keep
  `LOVABLE_API_KEY` out of browser code.
- **Connectors.** Lovable provides native connectors. `list_connectors` shows
  which are enabled; `add_connector` only returns the dashboard link. When a
  connector is enabled, calls go through the connector gateway and need
  `LOVABLE_API_KEY`. When Lovable does not provide one, users can bring their
  own key and store it in Secrets.
- **Secrets.** Lovable manages them. Do not put secret values in the repo.
<!-- LOVABLE:END -->

## Project rules
- Device rows are created only by the `confirm_pairing` SQL function via `/api/public/agent/pair`; clients may only read, rename (`name` column grant) or delete their own devices — keeps pairing trust on the server.
- Online status is derived from `last_heartbeat_at` freshness (`isDeviceOnline` in `src/lib/pairing.ts`), never set by the dashboard — only authenticated agent heartbeats count.
- Pairing secrets (6-digit code, QR token, device token) are stored only as SHA-256 hashes; `pairing_sessions` hash columns and `device_credentials` have no `authenticated` grants.
- Server functions live in `src/lib/api/*.functions.ts`; `src/server/**` is blocked from the client graph by import protection.
- Tables are created with Supabase default grants in this project: always `REVOKE ALL ... FROM anon, authenticated` before granting narrow privileges.
