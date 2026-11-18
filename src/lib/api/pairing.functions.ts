import { createServerFn } from "@tanstack/react-start";
import { z } from "zod";
import { requireSupabaseAuth } from "@/integrations/supabase/auth-middleware";
import { PAIRING_TTL_SECONDS, hashCode, hashToken, randomSixDigitCode, randomToken } from "@/lib/pairing";

/** Starts a pairing session. Secrets are returned once and only their hashes are stored. */
export const createPairingSession = createServerFn({ method: "POST" })
  .middleware([requireSupabaseAuth])
  .handler(async ({ context }) => {
    const { supabaseAdmin } = await import("@/integrations/supabase/client.server");
    const now = new Date().toISOString();
    // Expire stale sessions and cancel this user's other pending sessions.
    await supabaseAdmin.from("pairing_sessions").update({ status: "expired" }).eq("status", "pending").lt("expires_at", now);
    await supabaseAdmin.from("pairing_sessions").update({ status: "cancelled" }).eq("status", "pending").eq("user_id", context.userId);

    const token = randomToken();
    const expiresAt = new Date(Date.now() + PAIRING_TTL_SECONDS * 1000).toISOString();
    for (let attempt = 0; attempt < 5; attempt++) {
      const code = randomSixDigitCode();
      const { data, error } = await supabaseAdmin
        .from("pairing_sessions")
        .insert({ user_id: context.userId, code_hash: await hashCode(code), token_hash: await hashToken(token), expires_at: expiresAt })
        .select("id, expires_at")
        .single();
      if (!error && data) return { id: data.id, code, token, expiresAt: data.expires_at };
      if (error && error.code !== "23505") {
        console.error("createPairingSession", error);
        throw new Error("Could not start pairing. Please try again.");
      }
    }
    throw new Error("Could not start pairing. Please try again.");
  });

export const cancelPairingSession = createServerFn({ method: "POST" })
  .middleware([requireSupabaseAuth])
  .inputValidator((input: unknown) => z.object({ id: z.string().uuid() }).parse(input))
  .handler(async ({ data, context }) => {
    const { supabaseAdmin } = await import("@/integrations/supabase/client.server");
    const { error } = await supabaseAdmin
      .from("pairing_sessions")
      .update({ status: "cancelled" })
      .eq("id", data.id)
      .eq("user_id", context.userId)
      .eq("status", "pending");
    if (error) throw new Error("Could not cancel pairing.");
    return { ok: true };
  });
