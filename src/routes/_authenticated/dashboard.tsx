import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { History, List, Plus, QrCode } from "lucide-react";
import { AppShell } from "@/components/app/AppShell";
import { DeviceCard } from "@/components/app/DeviceCard";
import { EmptyDevices } from "@/components/app/EmptyDevices";
import { RemoveDeviceDialog } from "@/components/app/RemoveDeviceDialog";
import { Skeleton } from "@/components/ui/skeleton";
import { devicesQuery, type Device } from "@/lib/devices";

export const Route = createFileRoute("/_authenticated/dashboard")({
  head: () => ({ meta: [{ title: "Dashboard — Beacon Remote" }, { name: "description", content: "Your laptops at a glance." }] }),
  component: Dashboard,
});

const actions = [
  { to: "/pair", label: "Add Laptop", icon: Plus },
  { to: "/pair", label: "Pair New Device", icon: QrCode },
  { to: "/devices", label: "Registered Devices", icon: List },
  { to: "/history", label: "Connection History", icon: History },
] as const;

function Dashboard() {
  const { data, isLoading, isError, refetch } = useQuery(devicesQuery);
  const [removing, setRemoving] = useState<Device | null>(null);
  return (
    <AppShell>
      <section className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        {actions.map(({ to, label, icon: I }) => (
          <Link key={label} to={to} className="flex min-h-24 flex-col justify-between rounded-xl border bg-card p-3.5 transition-colors hover:bg-secondary">
            <I className="size-5 text-primary" aria-hidden />
            <span className="text-sm font-medium">{label}</span>
          </Link>
        ))}
      </section>

      <div className="mb-3 mt-8 flex items-baseline justify-between">
        <h2 className="text-lg font-semibold">Your laptops</h2>
        {data && data.length > 0 && <Link to="/devices" className="text-sm text-primary">See all</Link>}
      </div>
      {isLoading ? (
        <div className="space-y-3"><Skeleton className="h-36 rounded-xl" /><Skeleton className="h-36 rounded-xl" /></div>
      ) : isError ? (
        <div className="rounded-xl border p-4 text-sm">Couldn't load your laptops. <button className="text-primary" onClick={() => refetch()}>Try again</button></div>
      ) : data!.length === 0 ? (
        <EmptyDevices />
      ) : (
        <div className="grid gap-3 md:grid-cols-2">{data!.map((d) => <DeviceCard key={d.id} device={d} onRemove={setRemoving} />)}</div>
      )}
      <RemoveDeviceDialog device={removing} onClose={() => setRemoving(null)} />
    </AppShell>
  );
}
