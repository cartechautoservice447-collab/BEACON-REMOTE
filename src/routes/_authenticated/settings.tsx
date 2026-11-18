import type React from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { toast } from "sonner";
import { ChevronRight, LogOut, Moon, Sun } from "lucide-react";
import { AppShell, BackLink } from "@/components/app/AppShell";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { supabase } from "@/integrations/supabase/client";
import { useCurrentUser } from "@/hooks/use-session";
import { useSignOut } from "@/hooks/use-sign-out";
import { useTheme } from "@/hooks/use-theme";
import { devicesQuery } from "@/lib/devices";

export const Route = createFileRoute("/_authenticated/settings")({
  head: () => ({ meta: [{ title: "Settings — Beacon Remote" }, { name: "description", content: "Account, theme and device settings." }] }),
  component: Settings,
});

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-xl border bg-card p-4">
      <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted-foreground">{title}</h2>
      {children}
    </section>
  );
}

function Settings() {
  const user = useCurrentUser();
  const { theme, setTheme } = useTheme();
  const signOut = useSignOut();
  const qc = useQueryClient();
  const { data: devices } = useQuery(devicesQuery);
  const profile = useQuery({
    queryKey: ["profile", user?.id],
    enabled: !!user,
    queryFn: async () => {
      const { data, error } = await supabase.from("profiles").select("display_name").eq("id", user!.id).maybeSingle();
      if (error) throw error;
      return data;
    },
  });
  const [name, setName] = useState("");
  useEffect(() => { setName(profile.data?.display_name ?? ""); }, [profile.data]);
  const save = useMutation({
    mutationFn: async () => {
      const { error } = await supabase.from("profiles").update({ display_name: name.trim() || null }).eq("id", user!.id);
      if (error) throw error;
    },
    onSuccess: () => { toast.success("Saved"); qc.invalidateQueries({ queryKey: ["profile"] }); },
    onError: () => toast.error("Couldn't save."),
  });
  const submit = (e: FormEvent) => { e.preventDefault(); if (name.length > 80) { toast.error("Max 80 characters"); return; } save.mutate(); };

  return (
    <AppShell title="Settings" back={<BackLink to="/dashboard" label="Home" />}>
      <div className="space-y-4">
        <Section title="Account">
          <p className="text-sm text-muted-foreground">Email</p>
          <p className="mb-4 truncate">{user?.email ?? "…"}</p>
          <form onSubmit={submit} className="space-y-2">
            <Label htmlFor="dn">Display name</Label>
            <div className="flex gap-2">
              <Input id="dn" className="h-11" maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
              <Button type="submit" className="h-11" disabled={save.isPending || name === (profile.data?.display_name ?? "")}>Save</Button>
            </div>
          </form>
        </Section>

        <Section title="Appearance">
          <div className="grid grid-cols-2 gap-2" role="radiogroup" aria-label="Theme">
            {(["dark", "light"] as const).map((t) => (
              <button key={t} role="radio" aria-checked={theme === t} onClick={() => setTheme(t)}
                className={`flex h-12 items-center justify-center gap-2 rounded-lg border text-sm font-medium transition-colors ${theme === t ? "border-primary bg-accent text-accent-foreground" : "hover:bg-secondary"}`}>
                {t === "dark" ? <Moon className="size-4" /> : <Sun className="size-4" />}{t === "dark" ? "Dark" : "Light"}
              </button>
            ))}
          </div>
        </Section>

        <Section title="Devices">
          <Link to="/devices" className="flex min-h-11 items-center justify-between text-sm">
            <span>Manage registered laptops</span>
            <span className="flex items-center gap-1 text-muted-foreground">{devices?.length ?? 0}<ChevronRight className="size-4" /></span>
          </Link>
        </Section>

        <Section title="Security">
          <p className="text-sm text-muted-foreground">Two-step verification and active-session controls are planned for a later release.</p>
          <Link to="/auth" search={{ mode: "forgot" }} className="mt-3 inline-block text-sm text-primary" onClick={(e) => { e.preventDefault(); if (user?.email) supabase.auth.resetPasswordForEmail(user.email, { redirectTo: `${window.location.origin}/reset-password` }).then(({ error }) => error ? toast.error("Couldn't send the email.") : toast.success("Password reset email sent")); }}>
            Email me a password reset link
          </Link>
        </Section>

        <Section title="About">
          <p className="text-sm">Beacon Remote · version 0.1.0 (preview)</p>
          <p className="mt-1 text-sm text-muted-foreground">Remote control, audio, file transfer and clipboard sync are coming in later releases.</p>
        </Section>

        <Button variant="secondary" size="lg" className="h-12 w-full" onClick={() => void signOut()}><LogOut /> Sign out</Button>
      </div>
    </AppShell>
  );
}
