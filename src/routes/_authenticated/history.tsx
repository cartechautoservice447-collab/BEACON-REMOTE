import { createFileRoute } from "@tanstack/react-router";
import { History } from "lucide-react";
import { AppShell, BackLink } from "@/components/app/AppShell";

export const Route = createFileRoute("/_authenticated/history")({
  head: () => ({ meta: [{ title: "Connection history — Beacon Remote" }, { name: "description", content: "Your past remote sessions." }] }),
  component: () => (
    <AppShell title="Connection history" back={<BackLink to="/dashboard" label="Home" />}>
      <div className="rounded-2xl border border-dashed p-8 text-center">
        <History className="mx-auto size-8 text-muted-foreground" aria-hidden />
        <p className="mt-3 font-medium">No sessions yet</p>
        <p className="mt-1 text-sm text-muted-foreground">Remote sessions aren't available yet. Once they are, each one will be listed here.</p>
      </div>
    </AppShell>
  ),
});
