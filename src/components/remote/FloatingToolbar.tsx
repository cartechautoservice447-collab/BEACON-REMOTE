import { memo, useCallback, useLayoutEffect, useRef } from "react";
import { ChevronLeft, ChevronRight, Expand, GripVertical, Hand, Keyboard, Maximize, Minimize, Mouse, MoreHorizontal, Shrink, Unplug } from "lucide-react";
import type { FitMode, InputMode } from "@/lib/remote/protocol";
import { cn } from "@/lib/utils";

type Props = {
  mode: InputMode; onMode: (m: InputMode) => void;
  keyboardOpen: boolean; onKeyboard: () => void;
  fit: FitMode; onFit: () => void;
  fullscreen: boolean; fullscreenAvailable: boolean; onFullscreen: () => void;
  onMore: () => void; onDisconnect: () => void;
  collapsed: boolean; onCollapse: () => void;
  connection: "disconnected" | "connecting" | "connected";
};

const btn = "grid size-10 shrink-0 place-items-center rounded-lg text-foreground transition-colors hover:bg-secondary aria-pressed:bg-primary aria-pressed:text-primary-foreground disabled:opacity-40";

function formatTransform(x: number, y: number): string {
  return `translate3d(calc(-50% + ${x}px), ${y}px, 0)`;
}

/** Compact floating control bar. Drag the grip to move it; position is applied via transform only. */
export const FloatingToolbar = memo(function FloatingToolbar(p: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const pos = useRef({ x: 0, y: 0 });
  const drag = useRef<{ id: number; sx: number; sy: number; ox: number; oy: number } | null>(null);

  const clampAndApply = useCallback((nextX = pos.current.x, nextY = pos.current.y) => {
    const el = ref.current;
    if (!el) return;
    const parent = (el.offsetParent as HTMLElement | null) ?? el.parentElement;
    if (parent && (parent.clientWidth > 0 || parent.clientHeight > 0)) {
      const maxX = Math.max(0, (parent.clientWidth - el.offsetWidth) / 2);
      const maxY = Math.max(0, parent.clientHeight - el.offsetHeight - 16);
      pos.current = {
        x: Math.max(-maxX, Math.min(maxX, nextX)),
        y: Math.max(0, Math.min(maxY, nextY)),
      };
    } else {
      pos.current = { x: nextX, y: Math.max(0, nextY) };
    }
    el.style.transform = formatTransform(pos.current.x, pos.current.y);
  }, []);

  useLayoutEffect(() => {
    clampAndApply();
  }, [clampAndApply, p.collapsed, p.mode, p.keyboardOpen, p.connection]);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === "undefined") return;
    const parent = (el.offsetParent as HTMLElement | null) ?? el.parentElement;
    const ro = new ResizeObserver(() => clampAndApply());
    ro.observe(el);
    if (parent) ro.observe(parent);
    return () => ro.disconnect();
  }, [clampAndApply]);

  const onGripDown = (e: React.PointerEvent) => {
    e.preventDefault();
    try {
      (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    } catch {
      /* synthetic pointer */
    }
    const sx = Number.isFinite(e.clientX) ? e.clientX : 0;
    const sy = Number.isFinite(e.clientY) ? e.clientY : 0;
    drag.current = { id: e.pointerId, sx, sy, ox: pos.current.x, oy: pos.current.y };
  };
  const onGripMove = (e: React.PointerEvent) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    if (!Number.isFinite(e.clientX) || !Number.isFinite(e.clientY)) return;
    clampAndApply(d.ox + e.clientX - d.sx, d.oy + e.clientY - d.sy);
  };
  const onGripUp = () => { drag.current = null; };

  const status = { disconnected: ["bg-warning", "Not connected"], connecting: ["bg-warning", "Connecting…"], connected: ["bg-success", "Connected"] }[p.connection];

  return (
    <div ref={ref} role="toolbar" aria-label="Remote controls"
      className="absolute left-1/2 z-30 flex max-w-[calc(100%-1rem)] items-center gap-0.5 overflow-x-auto rounded-xl border bg-card/90 p-1 shadow-lg backdrop-blur"
      style={{ top: "max(0.5rem, env(safe-area-inset-top))" }}>
      <button type="button" aria-label="Move toolbar" className="grid h-10 w-6 shrink-0 cursor-grab touch-none place-items-center text-muted-foreground"
        onPointerDown={onGripDown} onPointerMove={onGripMove} onPointerUp={onGripUp} onPointerCancel={onGripUp}>
        <GripVertical className="size-4" />
      </button>
      <span className="flex shrink-0 items-center gap-1.5 px-1.5 text-xs" role="status" title="Session status" aria-label={`Session status: ${status[1]}`}>
        <span className={cn("size-2 rounded-full", status[0])} aria-hidden />
        <span className={cn(p.collapsed ? "sr-only" : "hidden min-[480px]:inline")}>{status[1]}</span>
      </span>
      {!p.collapsed && (
        <>
          <div className="mx-0.5 flex rounded-lg bg-secondary p-0.5" role="radiogroup" aria-label="Input mode">
            <button type="button" role="radio" aria-checked={p.mode === "mouse"} aria-label="Mouse Mode" onClick={() => p.onMode("mouse")}
              className={cn("flex h-9 items-center gap-1 rounded-md px-2 text-xs", p.mode === "mouse" && "bg-primary text-primary-foreground")}><Mouse className="size-4" /><span className="hidden sm:inline">Mouse</span></button>
            <button type="button" role="radio" aria-checked={p.mode === "touch"} aria-label="Touch Mode" onClick={() => p.onMode("touch")}
              className={cn("flex h-9 items-center gap-1 rounded-md px-2 text-xs", p.mode === "touch" && "bg-primary text-primary-foreground")}><Hand className="size-4" /><span className="hidden sm:inline">Touch</span></button>
          </div>
          <button type="button" className={btn} aria-label={p.keyboardOpen ? "Hide keyboard" : "Show keyboard"} aria-pressed={p.keyboardOpen} onClick={p.onKeyboard}><Keyboard className="size-5" /></button>
          <button type="button" className={btn} aria-label={p.fit === "fit" ? "Fill screen" : "Fit to screen"} onClick={p.onFit}>{p.fit === "fit" ? <Expand className="size-5" /> : <Shrink className="size-5" />}</button>
          <button type="button" className={btn} aria-label={p.fullscreen ? "Exit full screen" : "Full screen"} disabled={!p.fullscreenAvailable} title={p.fullscreenAvailable ? undefined : "This browser doesn't support full screen"} onClick={p.onFullscreen}>{p.fullscreen ? <Minimize className="size-5" /> : <Maximize className="size-5" />}</button>
          <button type="button" className={btn} aria-label="More controls" onClick={p.onMore}><MoreHorizontal className="size-5" /></button>
          <button type="button" className={cn(btn, "text-destructive")} aria-label="Disconnect" onClick={p.onDisconnect}><Unplug className="size-5" /></button>
        </>
      )}
      <button type="button" className={cn(btn, "w-7")} aria-label={p.collapsed ? "Expand toolbar" : "Collapse toolbar"} onClick={p.onCollapse}>
        {p.collapsed ? <ChevronRight className="size-4" /> : <ChevronLeft className="size-4" />}
      </button>
    </div>
  );
});
