import { createFileRoute } from "@tanstack/react-router";
import { z } from "zod";
import { MAX_FAILED_PER_CLIENT, hashCode, hashDeviceToken, hashToken, randomToken, sha256Hex } from "@/lib/pairing";

// Companion agent endpoint: exchanges a 6-digit code (or QR token) for a device credential.
const deviceSchema = z.object({
  name: z.string().trim().min(1).max(64),
  os: z.string().trim().min(1).max(64),
  agent_version: z.string().trim().min(1).max(32),
});
const bodySchema = z.union([
  z.object({ code: z.string().regex(/^\d{6}$/), device: deviceSchema }),
  z.object({ pairing_id: z.string().uuid(), token: z.string().min(20).max(128), device: deviceSchema }),
]);

const json = (body: unknown, status = 200) => Response.json(body, { status, headers: { "cache-control": "no-store" } });

export const Route = createFileRoute("/api/public/agent/pair")({
  server: {
    handlers: {
      POST: async ({ request }) => {
        let parsed;
        try {
          parsed = bodySchema.safeParse(await request.json());
        } catch {
          return json({ error: "invalid_json" }, 400);
        }
        if (!parsed.success) return json({ error: "invalid_request" }, 400);
        const body = parsed.data;

        const { supabaseAdmin } = await import("@/integrations/supabase/client.server");
        const ip = request.headers.get("cf-connecting-ip") ?? request.headers.get("x-forwarded-for") ?? "unknown";
        const clientKey = await sha256Hex(`client:${ip}`);
        const since = new Date(Date.now() - 15 * 60_000).toISOString();
        const { count } = await supabaseAdmin
          .from("pairing_attempts").select("id", { count: "exact", head: true })
          .eq("client_key", clientKey).gte("created_at", since);
        if ((count ?? 0) >= MAX_FAILED_PER_CLIENT) return json({ error: "too_many_attempts" }, 429);

        const deviceToken = randomToken(32);
        const { data, error } = await supabaseAdmin.rpc("confirm_pairing", {
          _code_hash: "code" in body ? await hashCode(body.code) : "",
          _pairing_id: "pairing_id" in body ? body.pairing_id : (null as unknown as string),
          _token_hash: "token" in body ? await hashToken(body.token) : "",
          _name: body.device.name,
          _os: body.device.os,
          _agent_version: body.device.agent_version,
          _device_token_hash: await hashDeviceToken(deviceToken),
        });
        if (error) {
          console.error("confirm_pairing", error);
          return json({ error: "server_error" }, 500);
        }
        const row = Array.isArray(data) ? data[0] : data;
        if (!row || row.result !== "confirmed") {
          await supabaseAdmin.from("pairing_attempts").insert({ client_key: clientKey });
          return json({ error: row?.result === "expired" ? "expired" : "invalid_code" }, row?.result === "expired" ? 410 : 401);
        }
        // The device token is shown only once; the agent must store it securely.
        return json({ device_id: row.device_id, device_token: deviceToken });
      },
    },
  },
});
