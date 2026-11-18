import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { AppShell, BackLink } from "@/components/app/AppShell";
import { DeviceCard } from "@/components/app/DeviceCard";
import { EmptyDevices } from "@/components/app/EmptyDevices";
import { RemoveDeviceDialog } from "@/components/app/RemoveDeviceDialog";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { devicesQuery, type Device } from "@/lib/devices";

export const Route = createFileRoute("/_authenticated/devices/")({
  head: () => ({ meta: [{ title: "Registered devices — Beacon Remote" }, { name: "description", content: "All laptops paired with your account." }] }),
  component: Devices,
});

function Devices() {
  const { data, isLoading } = useQuery(devicesQuery);
  const [removing, setRemoving] = useState<Device | null>(null);
  return (
    <AppShell title="Registered devices" back={<BackLink to="/dashboard" label="Home" />}>
      {isLoading ? <Skeleton className="h-36 rounded-xl" /> : !data?.length ? <EmptyDevices /> : (
        <>
          <div className="grid gap-3 md:grid-cols-2">{data.map((d) => <DeviceCard key={d.id} device={d} onRemove={setRemoving} />)}</div>
          <Button asChild variant="secondary" className="mt-5 w-full"><Link to="/pair">Pair another laptop</Link></Button>
        </>
      )}
      <RemoveDeviceDialog device={removing} onClose={() => setRemoving(null)} />
    </AppShell>
  );
}
