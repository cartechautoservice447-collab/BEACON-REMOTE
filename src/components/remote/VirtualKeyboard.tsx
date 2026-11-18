import { memo, useSyncExternalStore } from "react";
import { KEY_BY_ID } from "@/lib/remote/keys";
import type { KeyboardEngine } from "@/lib/remote/keyboard-engine";
import type { KeyboardLayout } from "@/lib/remote/keyboard-layout";
import { cn } from "@/lib/utils";

type Props = { layout: KeyboardLayout; engine: KeyboardEngine; compact?: boolean };

/** On-screen laptop keyboard. Each key sends key-down on touch and key-up on release. */
export const VirtualKeyboard = memo(function VirtualKeyboard({ layout, engine, compact }: Props) {
  const snap = useSyncExternalStore(engine.subscribe, engine.getSnapshot, engine.getSnapshot);
  const shifted = Boolean(snap.modifiers["shift"]);
  return (
    <div
      role="group"
      aria-label={`${layout.name} — key presses are not sent until the laptop is connected`}
      className="max-h-[46dvh] select-none overflow-y-auto overscroll-contain touch-pan-y border-t bg-card/95 px-1 pt-1"
      style={{
        paddingBottom: "max(0.25rem, env(safe-area-inset-bottom))",
        paddingLeft: "max(0.25rem, env(safe-area-inset-left))",
        paddingRight: "max(0.25rem, env(safe-area-inset-right))",
      }}
    >
      {layout.rows.map((row, ri) => (
        <div key={ri} className="flex gap-1 pb-1">
          {row.map((id) => {
            const def = KEY_BY_ID[id];
            if (!def) return null;
            const mod = snap.modifiers[id];
            const active = mod || (id === "caps" && snap.capsOn) || snap.pressed.includes(id);
            const label = shifted && def.shiftLabel ? def.shiftLabel : def.label;
            return (
              <button
                key={id}
                type="button"
                aria-label={def.label}
                aria-pressed={def.kind !== "key" ? Boolean(active) : undefined}
                onPointerDown={(e) => {
                  e.preventDefault();
                  try {
                    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
                  } catch {
                    /* synthetic pointer */
                  }
                  engine.press(id);
                }}
                onPointerUp={() => engine.release(id)}
                onPointerCancel={() => engine.release(id)}
                onLostPointerCapture={() => engine.release(id)}
                onPointerLeave={(e) => {
                  if (!e.currentTarget.hasPointerCapture?.(e.pointerId)) engine.release(id);
                }}
                onContextMenu={(e) => e.preventDefault()}
                style={{ flexGrow: def.width ?? 1, flexBasis: 0 }}
                className={cn(
                  "min-w-0 touch-none truncate rounded-md border bg-secondary px-0.5 text-secondary-foreground",
                  compact
                    ? "h-7 text-[11px] [@media(min-height:540px)]:h-9 [@media(min-height:540px)]:text-xs"
                    : "h-7 text-[11px] [@media(min-height:540px)]:h-8 [@media(min-height:540px)]:text-xs [@media(min-height:700px)]:sm:h-10 [@media(min-height:700px)]:sm:text-sm",
                  active && "border-primary bg-primary text-primary-foreground",
                  mod === "locked" && "ring-2 ring-ring",
                )}
              >
                {label}
              </button>
            );
          })}
        </div>
      ))}
    </div>
  );
});
