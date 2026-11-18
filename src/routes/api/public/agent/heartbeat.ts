import { createFileRoute } from "@tanstack/react-router";
import { hashDeviceToken } from "@/lib/pairing";

// Companion agent heartbeat. Only an authenticated agent token can mark a device online.
export const Route = createFileRoute("/api/public/agent/heartbeat")({
  server: {
    handlers: {
      POST: async ({ request }) => {
        const auth = request.headers.get("authorization") ?? "";
        const token = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";
        if (token.length < 20 || token.length > 128) return Response.json({ error: "unauthorized" }, { status: 401 });

        const { supabaseAdmin } = await import("@/integrations/supabase/client.server");
        const { data: cred } = await supabaseAdmin
          .from("device_credentials").select("device_id").eq("token_hash", await hashDeviceToken(token)).maybeSingle();
        if (!cred) return Response.json({ error: "unauthorized" }, { status: 401 });

        const now = new Date().toISOString();
        const { error } = await supabaseAdmin
          .from("devices").update({ status: "online", last_heartbeat_at: now }).eq("id", cred.device_id);
        if (error) return Response.json({ error: "server_error" }, { status: 500 });
        return Response.json({ ok: true, server_time: now });
      },
    },
  },
});
