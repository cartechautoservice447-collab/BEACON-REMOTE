import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useEffect, useState, type FormEvent } from "react";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Logo } from "@/components/app/Logo";
import { supabase } from "@/integrations/supabase/client";
import { lovable } from "@/integrations/lovable/index";

type Mode = "signin" | "signup" | "forgot";

export const Route = createFileRoute("/auth")({
  validateSearch: (s: Record<string, unknown>): { mode?: Mode | undefined } => ({
    mode: s["mode"] === "signup" || s["mode"] === "forgot" ? s["mode"] : undefined,
  }),
  head: () => ({
    meta: [
      { title: "Sign in — Beacon Remote" },
      { name: "description", content: "Sign in or create your Beacon Remote account." },
      { property: "og:title", content: "Sign in — Beacon Remote" },
      { property: "og:description", content: "Sign in or create your Beacon Remote account." },
    ],
  }),
  component: AuthPage,
});

const emailSchema = z.string().trim().email("Enter a valid email address").max(255);
const passwordSchema = z.string().min(8, "Use at least 8 characters").max(72);

function friendly(msg: string) {
  if (/invalid login/i.test(msg)) return "Email or password is incorrect.";
  if (/not confirmed/i.test(msg)) return "Please confirm your email first — check your inbox.";
  if (/already registered/i.test(msg)) return "An account with this email already exists. Try signing in.";
  if (/rate limit|too many/i.test(msg)) return "Too many attempts. Please wait a moment and try again.";
  if (/pwned|weak|leaked/i.test(msg)) return "This password is too easy to guess. Please choose another.";
  return msg;
}

function AuthPage() {
  const search = Route.useSearch();
  const navigate = useNavigate();
  const [mode, setMode] = useState<Mode>(search.mode ?? "signin");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    supabase.auth.getSession().then(({ data }) => {
      if (data.session) navigate({ to: "/dashboard", replace: true });
    });
    const { data } = supabase.auth.onAuthStateChange((_e, session) => {
      if (session) navigate({ to: "/dashboard", replace: true });
    });
    return () => data.subscription.unsubscribe();
  }, [navigate]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setNotice(null);
    const em = emailSchema.safeParse(email);
    if (!em.success) return setError(em.error.issues[0]!.message);
    if (mode !== "forgot") {
      const pw = passwordSchema.safeParse(password);
      if (!pw.success && mode === "signup") return setError(pw.error.issues[0]!.message);
      if (!password) return setError("Enter your password");
    }
    setBusy(true);
    try {
      if (mode === "signin") {
        const { error } = await supabase.auth.signInWithPassword({ email: em.data, password });
        if (error) throw error;
      } else if (mode === "signup") {
        const { data, error } = await supabase.auth.signUp({
          email: em.data, password, options: { emailRedirectTo: `${window.location.origin}/dashboard` },
        });
        if (error) throw error;
        if (!data.session) setNotice("Check your email and tap the confirmation link to finish creating your account.");
      } else {
        const { error } = await supabase.auth.resetPasswordForEmail(em.data, { redirectTo: `${window.location.origin}/reset-password` });
        if (error) throw error;
        setNotice("If an account exists for that email, a reset link is on its way.");
      }
    } catch (err) {
      setError(friendly(err instanceof Error ? err.message : "Something went wrong"));
    } finally {
      setBusy(false);
    }
  };

  const google = async () => {
    const r = await lovable.auth.signInWithOAuth("google", { redirect_uri: window.location.origin + "/auth" });
    if (r.error) toast.error("Google sign-in failed. Please try again.");
  };

  const titles = { signin: "Welcome back", signup: "Create your account", forgot: "Reset your password" };

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center bg-background px-5 py-10">
      <div className="w-full max-w-sm">
        <Link to="/" className="mb-8 inline-block"><Logo /></Link>
        <h1 className="text-2xl font-semibold">{titles[mode]}</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          {mode === "forgot" ? "We'll email you a link to choose a new password." : "Manage your laptops from your phone."}
        </p>

        <form onSubmit={submit} className="mt-6 space-y-4" noValidate>
          <div className="space-y-1.5">
            <Label htmlFor="email">Email</Label>
            <Input id="email" type="email" autoComplete="email" inputMode="email" className="h-11" value={email} onChange={(e) => setEmail(e.target.value)} />
          </div>
          {mode !== "forgot" && (
            <div className="space-y-1.5">
              <div className="flex items-center justify-between">
                <Label htmlFor="password">Password</Label>
                {mode === "signin" && (
                  <button type="button" className="text-xs text-primary" onClick={() => { setMode("forgot"); setError(null); }}>Forgot password?</button>
                )}
              </div>
              <Input id="password" type="password" className="h-11" autoComplete={mode === "signup" ? "new-password" : "current-password"} value={password} onChange={(e) => setPassword(e.target.value)} />
              {mode === "signup" && <p className="text-xs text-muted-foreground">At least 8 characters.</p>}
            </div>
          )}
          {error && <p role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</p>}
          {notice && <p role="status" className="rounded-lg bg-accent px-3 py-2 text-sm text-accent-foreground">{notice}</p>}
          <Button type="submit" size="lg" className="h-11 w-full" disabled={busy}>
            {busy ? "Please wait…" : mode === "signin" ? "Sign in" : mode === "signup" ? "Create account" : "Send reset link"}
          </Button>
        </form>

        {mode !== "forgot" && (
          <>
            <div className="my-5 flex items-center gap-3 text-xs text-muted-foreground"><span className="h-px flex-1 bg-border" />or<span className="h-px flex-1 bg-border" /></div>
            <Button type="button" variant="outline" size="lg" className="h-11 w-full" onClick={google}>
              <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24"><path fill="#4285F4" d="M23.49 12.27c0-.79-.07-1.54-.19-2.27H12v4.51h6.47c-.29 1.48-1.14 2.73-2.4 3.58v3h3.86c2.26-2.09 3.56-5.17 3.56-8.82z"/><path fill="#34A853" d="M12 24c3.24 0 5.95-1.08 7.93-2.91l-3.86-3c-1.08.72-2.45 1.16-4.07 1.16-3.13 0-5.78-2.11-6.73-4.96H1.29v3.09C3.26 21.3 7.31 24 12 24z"/><path fill="#FBBC05" d="M5.27 14.29c-.25-.72-.38-1.49-.38-2.29s.14-1.57.38-2.29V6.62H1.29C.47 8.24 0 10.06 0 12s.47 3.76 1.29 5.38l3.98-3.09z"/><path fill="#EA4335" d="M12 4.75c1.77 0 3.35.61 4.6 1.8l3.42-3.42C17.95 1.19 15.24 0 12 0 7.31 0 3.26 2.7 1.29 6.62l3.98 3.09c.95-2.85 3.6-4.96 6.73-4.96z"/></svg>
              Continue with Google
            </Button>
          </>
        )}

        <p className="mt-6 text-center text-sm text-muted-foreground">
          {mode === "signin" ? (
            <>New here? <button className="font-medium text-primary" onClick={() => { setMode("signup"); setError(null); setNotice(null); }}>Create an account</button></>
          ) : (
            <>Already have an account? <button className="font-medium text-primary" onClick={() => { setMode("signin"); setError(null); setNotice(null); }}>Sign in</button></>
          )}
        </p>
      </div>
    </div>
  );
}
