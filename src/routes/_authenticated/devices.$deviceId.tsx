import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type FormEvent } from "react";
import { toast } from "sonner";
import { AppShell, BackLink } from "@/components/app/AppShell";
import { StatusBadge } from "@/components/app/DeviceCard";
import { RemoveDeviceDialog } from "@/components/app/RemoveDeviceDialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { supabase } from "@/integrations/supabase/client";
import { deviceQuery, formatRelative, type Device } from "@/lib/devices";

export const Route = createFileRoute("/_authenticated/devices/$deviceId")({
  head: () => ({ meta: [{ title: "Device settings — Beacon Remote" }, { name: "description", content: "Rename or remove a paired laptop." }] }),
  component: DeviceSettings,
});

function DeviceSettings() {
  const { deviceId } = Route.useParams();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const { data: device, isLoading } = useQuery(deviceQuery(deviceId));
  const [name, setName] = useState("");
  const [removing, setRemoving] = useState<Device | null>(null);
  useEffect(() => { if (device) setName(device.name); }, [device]);

  const rename = useMutation({
    mutationFn: async (n: string) => {
      const { error } = await supabase.from("devices").update({ name: n }).eq("id", deviceId);
      if (error) throw error;
    },
    onSuccess: () => { toast.success("Name saved"); qc.invalidateQueries({ queryKey: ["devices"] }); },
    onError: () => toast.error("Couldn't save the name."),
  });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const n = name.trim();
    if (n.length < 1 || n.length > 64) { toast.error("Name must be 1–64 characters"); return; }
    rename.mutate(n);
  };

  return (
    <AppShell title="Device settings" back={<BackLink to="/devices" label="Devices" />}>
      {isLoading ? <Skeleton className="h-48 rounded-xl" /> : !device ? (
        <p className="text-muted-foreground">This laptop isn't linked to your account.</p>
      ) : (
        <div className="space-y-5">
          <dl className="grid grid-cols-2 gap-3 rounded-xl border bg-card p-4 text-sm">
            <div><dt className="text-muted-foreground">Status</dt><dd className="mt-1"><StatusBadge device={device} /></dd></div>
            <div><dt className="text-muted-foreground">System</dt><dd className="mt-1">{device.os}</dd></div>
            <div><dt className="text-muted-foreground">App version</dt><dd className="mt-1">{device.agent_version}</dd></div>
            <div><dt className="text-muted-foreground">Last seen</dt><dd className="mt-1">{formatRelative(device.last_heartbeat_at)}</dd></div>
            <div><dt className="text-muted-foreground">Last connected</dt><dd className="mt-1">{formatRelative(device.last_connected_at)}</dd></div>
            <div><dt className="text-muted-foreground">Added</dt><dd className="mt-1">{new Date(device.registered_at).toLocaleDateString()}</dd></div>
          </dl>
          <form onSubmit={submit} className="space-y-2 rounded-xl border bg-card p-4">
            <Label htmlFor="name">Laptop name</Label>
            <div className="flex gap-2">
              <Input id="name" className="h-11" maxLength={64} value={name} onChange={(e) => setName(e.target.value)} />
              <Button type="submit" className="h-11" disabled={rename.isPending || name.trim() === device.name}>Save</Button>
            </div>
          </form>
          <div className="rounded-xl border border-destructive/30 p-4">
            <p className="font-medium">Remove this laptop</p>
            <p className="mt-1 text-sm text-muted-foreground">It will stop being able to connect until you pair it again.</p>
            <Button variant="destructive" className="mt-3" onClick={() => setRemoving(device)}>Remove laptop</Button>
          </div>
        </div>
      )}
      <RemoveDeviceDialog device={removing} onClose={() => setRemoving(null)} onRemoved={() => navigate({ to: "/devices" })} />
    </AppShell>
  );
}
