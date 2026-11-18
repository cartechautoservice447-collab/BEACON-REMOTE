import { Radio } from "lucide-react";

export function Logo({ compact = false }: { compact?: boolean }) {
  return (
    <span className="inline-flex items-center gap-2">
      <span className="grid size-8 place-items-center rounded-lg bg-primary text-primary-foreground">
        <Radio className="size-4" aria-hidden />
      </span>
      {!compact && <span className="font-display text-base font-semibold tracking-tight">Beacon Remote</span>}
    </span>
  );
}
