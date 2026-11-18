import { createFileRoute, Link } from "@tanstack/react-router";
import { useServerFn } from "@tanstack/react-start";
import { useQueryClient } from "@tanstack/react-query";
import type React from "react";
import { useCallback, useEffect, useRef, useState } from "react";
import { QRCodeSVG } from "qrcode.react";
import { AlertTriangle, CheckCircle2, Clock, Loader2, XCircle } from "lucide-react";
import { AppShell, BackLink } from "@/components/app/AppShell";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import { supabase } from "@/integrations/supabase/client";
import { cancelPairingSession, createPairingSession } from "@/lib/api/pairing.functions";
import { PAIRING_TTL_SECONDS, secondsRemaining } from "@/lib/pairing";

export const Route = createFileRoute("/_authenticated/pair")({
  head: () => ({ meta: [{ title: "Pair a laptop — Beacon Remote" }, { name: "description", content: "Link a Windows laptop to your account." }] }),
  component: PairPage,
});

type Session = { id: string; code: string; token: string; expiresAt: string };
type Phase = "intro" | "creating" | "waiting" | "success" | "expired" | "cancelled" | "failed" | "error";

function PairPage() {
  const create = useServerFn(createPairingSession);
  const cancel = useServerFn(cancelPairingSession);
  const qc = useQueryClient();
  const [phase, setPhase] = useState<Phase>("intro");
  const [session, setSession] = useState<Session | null>(null);
  const [left, setLeft] = useState(PAIRING_TTL_SECONDS);
  const [errorMsg, setErrorMsg] = useState("");
  const phaseRef = useRef(phase);
  phaseRef.current = phase;

  const start = async () => {
    setPhase("creating");
    try {
      const s = await create();
      setSession(s);
      setLeft(secondsRemaining(s.expiresAt));
      setPhase("waiting");
    } catch (e) {
      setErrorMsg(e instanceof Error ? e.message : "Could not start pairing.");
      setPhase("error");
    }
  };

  const applyStatus = useCallback((status: string) => {
    if (phaseRef.current !== "waiting") return;
    if (status === "confirmed") { setPhase("success"); qc.invalidateQueries({ queryKey: ["devices"] }); }
    else if (status === "expired") setPhase("expired");
    else if (status === "cancelled") setPhase("cancelled");
    else if (status === "failed") setPhase("failed");
  }, [qc]);

  // Listen for backend confirmation (realtime + polling fallback).
  useEffect(() => {
    if (phase !== "waiting" || !session) return;
    const channel = supabase
      .channel(`pairing-${session.id}-${Math.random().toString(36).slice(2)}`)
      .on("postgres_changes", { event: "UPDATE", schema: "public", table: "pairing_sessions", filter: `id=eq.${session.id}` },
        (p) => applyStatus((p.new as { status: string }).status))
      .subscribe();
    const poll = setInterval(async () => {
      const { data } = await supabase.from("pairing_sessions").select("status").eq("id", session.id).maybeSingle();
      if (data) applyStatus(data.status);
    }, 5000);
    const tick = setInterval(() => {
      const s = secondsRemaining(session.expiresAt);
      setLeft(s);
      if (s === 0) setPhase("expired");
    }, 1000);
    return () => { supabase.removeChannel(channel); clearInterval(poll); clearInterval(tick); };
  }, [phase, session, applyStatus]);

  const doCancel = async () => {
    if (!session) return;
    setPhase("cancelled");
    try { await cancel({ data: { id: session.id } }); } catch { /* session expires anyway */ }
  };

  const qrValue = session
    ? JSON.stringify({ v: 1, type: "beacon-pair", server: window.location.origin, pairing_id: session.id, token: session.token })
    : "";
  const mm = String(Math.floor(left / 60)).padStart(1, "0");
  const ss = String(left % 60).padStart(2, "0");

  return (
    <AppShell title="Pair a laptop" back={<BackLink to="/dashboard" label="Home" />}>
      {phase === "intro" && (
        <div className="space-y-5">
          <ol className="space-y-3 rounded-xl border bg-card p-4 text-sm">
            <li><b>1.</b> Open the Beacon companion app on your Windows laptop.</li>
            <li><b>2.</b> Tap the button below to get a QR code and a 6-digit code.</li>
            <li><b>3.</b> Scan the QR code with the laptop's camera, or type the code into the laptop app.</li>
            <li><b>4.</b> Keep this screen open — it updates once the laptop confirms.</li>
          </ol>
          <p className="rounded-lg bg-warning/15 px-3 py-2 text-sm">
            The Windows companion app is still being built, so pairing can't complete yet. You can still try the steps — the code will expire after 10 minutes.
          </p>
          <Button size="lg" className="h-12 w-full" onClick={start}>Get pairing code</Button>
        </div>
      )}

      {phase === "creating" && (
        <div className="flex items-center justify-center gap-2 py-20 text-muted-foreground"><Loader2 className="size-5 animate-spin" /> Preparing a secure code…</div>
      )}

      {phase === "waiting" && session && (
        <div className="space-y-5">
          <div className="flex flex-col items-center rounded-2xl border bg-card p-5">
            <div className="rounded-xl bg-white p-3"><QRCodeSVG value={qrValue} size={200} level="M" /></div>
            <p className="mt-5 text-xs uppercase tracking-wider text-muted-foreground">Or enter this code on your laptop</p>
            <p className="mt-1 font-display text-4xl font-semibold tracking-[0.3em] tabular-nums" aria-label={`Code ${session.code.split("").join(" ")}`}>
              {session.code.slice(0, 3)} {session.code.slice(3)}
            </p>
          </div>
          <div className="rounded-xl border bg-card p-4">
            <div className="flex items-center justify-between text-sm">
              <span className="inline-flex items-center gap-2"><Loader2 className="size-4 animate-spin text-primary" /> Waiting for your laptop…</span>
              <span className="inline-flex items-center gap-1 tabular-nums text-muted-foreground"><Clock className="size-4" />{mm}:{ss}</span>
            </div>
            <Progress className="mt-3 h-1.5" value={(left / PAIRING_TTL_SECONDS) * 100} />
            <p className="mt-2 text-xs text-muted-foreground">Single-use code. It expires automatically.</p>
          </div>
          <Button variant="secondary" size="lg" className="h-12 w-full" onClick={doCancel}>Cancel pairing</Button>
        </div>
      )}

      {phase === "success" && (
        <Result icon={<CheckCircle2 className="size-10 text-success" />} title="Laptop paired" text="Your laptop is now linked to your account.">
          <Button asChild size="lg" className="w-full"><Link to="/devices">View devices</Link></Button>
        </Result>
      )}
      {phase === "expired" && (
        <Result icon={<Clock className="size-10 text-warning" />} title="Code expired" text="The laptop didn't confirm in time. Get a new code to try again.">
          <Button size="lg" className="w-full" onClick={start}>Get a new code</Button>
        </Result>
      )}
      {phase === "cancelled" && (
        <Result icon={<XCircle className="size-10 text-muted-foreground" />} title="Pairing cancelled" text="The code can no longer be used.">
          <Button size="lg" className="w-full" onClick={start}>Start again</Button>
        </Result>
      )}
      {phase === "failed" && (
        <Result icon={<AlertTriangle className="size-10 text-destructive" />} title="Too many wrong attempts" text="For your safety this code was locked. Get a new code to try again.">
          <Button size="lg" className="w-full" onClick={start}>Get a new code</Button>
        </Result>
      )}
      {phase === "error" && (
        <Result icon={<AlertTriangle className="size-10 text-destructive" />} title="Something went wrong" text={errorMsg}>
          <Button size="lg" className="w-full" onClick={start}>Try again</Button>
        </Result>
      )}
    </AppShell>
  );
}

function Result({ icon, title, text, children }: { icon: React.ReactNode; title: string; text: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col items-center rounded-2xl border bg-card p-8 text-center">
      {icon}
      <h2 className="mt-3 text-xl font-semibold">{title}</h2>
      <p className="mt-1 max-w-xs text-sm text-muted-foreground">{text}</p>
      <div className="mt-6 w-full max-w-xs">{children}</div>
    </div>
  );
}
