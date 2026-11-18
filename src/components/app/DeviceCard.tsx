import { Link } from "@tanstack/react-router";
import { Laptop, Settings2, Trash2, MonitorPlay } from "lucide-react";
import { Button } from "@/components/ui/button";
import { type Device, formatRelative } from "@/lib/devices";
import { isDeviceOnline } from "@/lib/pairing";

export function StatusBadge({ device }: { device: Device }) {
  const online = isDeviceOnline(device.last_heartbeat_at);
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium ${online ? "bg-success/15 text-success" : "bg-muted text-muted-foreground"}`}>
      <span className={`size-1.5 rounded-full ${online ? "bg-success" : "bg-muted-foreground/60"}`} aria-hidden />
      {online ? "Online" : "Offline"}
    </span>
  );
}

export function DeviceCard({ device, onRemove }: { device: Device; onRemove: (d: Device) => void }) {
  return (
    <article className="rounded-xl border bg-card p-4">
      <div className="flex items-start gap-3">
        <span className="grid size-10 shrink-0 place-items-center rounded-lg bg-accent text-accent-foreground">
          <Laptop className="size-5" aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="truncate text-base font-semibold">{device.name}</h3>
            <StatusBadge device={device} />
          </div>
          <p className="mt-0.5 text-sm text-muted-foreground">{device.os}</p>
          <p className="text-xs text-muted-foreground">Last connected: {formatRelative(device.last_connected_at)}</p>
        </div>
      </div>
      <div className="mt-4 flex items-center gap-2">
        <Button asChild className="flex-1">
          <Link to="/remote/$deviceId" params={{ deviceId: device.id }}>
            <MonitorPlay /> Connect
          </Link>
        </Button>
        <Button asChild variant="secondary" size="icon" aria-label={`Settings for ${device.name}`}>
          <Link to="/devices/$deviceId" params={{ deviceId: device.id }}><Settings2 /></Link>
        </Button>
        <Button variant="secondary" size="icon" aria-label={`Remove ${device.name}`} onClick={() => onRemove(device)}>
          <Trash2 />
        </Button>
      </div>
      <p className="mt-2 text-xs text-muted-foreground">Opens the remote workspace. Live screen streaming and remote input delivery are not yet connected.</p>
    </article>
  );
}
