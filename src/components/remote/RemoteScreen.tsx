import { memo, useEffect, useRef } from "react";
import { MonitorOff } from "lucide-react";
import { applyView, contentRect, fromRemote, IDENTITY_VIEW, type Rect, type Size, type ViewTransform } from "@/lib/remote/coords";
import { MouseEngine, type MouseSettings } from "@/lib/remote/mouse-engine";
import { TouchEngine } from "@/lib/remote/touch-engine";
import { browserClock, type FitMode, type InputMode } from "@/lib/remote/protocol";
import type { EngineHost, InputEngine } from "@/lib/remote/engine-host";
import type { InputBus } from "@/lib/remote/transport";

/** Used only for laying out the placeholder; the real size comes from the stream. */
const PLACEHOLDER_SIZE: Size = { w: 1920, h: 1080 };

type Props = {
  mode: InputMode;
  fit: FitMode;
  bus: InputBus;
  mouseSettings: MouseSettings;
  /** Future WebRTC stream. Null = nothing to show, never faked. */
  stream: MediaStream | null;
  deviceName: string;
  onEngine?: (e: MouseEngine | TouchEngine | null) => void;
};

/**
 * Renders the remote frame with correct aspect ratio and routes pointer input to
 * the active engine. Geometry lives in refs and transforms are written straight to
 * the DOM, so gestures and resizes never trigger React re-renders.
 */
export const RemoteScreen = memo(function RemoteScreen({ mode, fit, bus, mouseSettings, stream, deviceName, onEngine }: Props) {
  const surfaceRef = useRef<HTMLDivElement>(null);
  const layerRef = useRef<HTMLDivElement>(null);
  const frameRef = useRef<HTMLDivElement>(null);
  const videoRef = useRef<HTMLVideoElement>(null);
  const cursorRef = useRef<HTMLDivElement>(null);
  const geo = useRef({ container: { w: 0, h: 0 } as Size, base: { x: 0, y: 0, w: 0, h: 0 } as Rect, view: IDENTITY_VIEW as ViewTransform, left: 0, top: 0, source: PLACEHOLDER_SIZE });
  const fitRef = useRef(fit);
  const engineRef = useRef<InputEngine | null>(null);
  const settingsRef = useRef(mouseSettings);
  settingsRef.current = mouseSettings;
  if (engineRef.current instanceof MouseEngine) engineRef.current.settings = mouseSettings;
  if (engineRef.current instanceof TouchEngine) engineRef.current.scrollSpeed = mouseSettings.scrollSpeed;

  const paintCursor = (nx: number, ny: number) => {
    const c = cursorRef.current; if (!c) return;
    const p = fromRemote(nx, ny, applyView(geo.current.base, geo.current.view));
    c.style.transform = `translate3d(${p.x}px, ${p.y}px, 0)`;
    c.style.opacity = "1";
  };
  const applyLayout = () => {
    const g = geo.current;
    g.base = contentRect(g.container, g.source, fitRef.current);
    const f = frameRef.current;
    if (f) { f.style.left = `${g.base.x}px`; f.style.top = `${g.base.y}px`; f.style.width = `${g.base.w}px`; f.style.height = `${g.base.h}px`; }
    if (layerRef.current) layerRef.current.style.transform = `translate3d(${g.view.panX}px, ${g.view.panY}px, 0) scale(${g.view.zoom})`;
  };

  // Resize / rotation: recompute geometry once per observer callback.
  useEffect(() => {
    const el = surfaceRef.current!;
    const measure = () => {
      const r = el.getBoundingClientRect();
      Object.assign(geo.current, { container: { w: r.width, h: r.height }, left: r.left, top: r.top, view: IDENTITY_VIEW });
      applyLayout();
    };
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    window.addEventListener("orientationchange", measure);
    window.visualViewport?.addEventListener("resize", measure);
    measure();
    return () => { ro.disconnect(); window.removeEventListener("orientationchange", measure); window.visualViewport?.removeEventListener("resize", measure); };
  }, []);

  useEffect(() => { fitRef.current = fit; geo.current.view = IDENTITY_VIEW; applyLayout(); }, [fit]);

  // Attach the stream (when one exists) and dispose it on change/unmount.
  useEffect(() => {
    const v = videoRef.current; if (!v) return;
    const currentGeo = geo.current;
    v.srcObject = stream;
    const onMeta = () => { if (v.videoWidth && v.videoHeight) { currentGeo.source = { w: v.videoWidth, h: v.videoHeight }; applyLayout(); } };
    v.addEventListener("loadedmetadata", onMeta);
    v.addEventListener("resize", onMeta);
    return () => { v.removeEventListener("loadedmetadata", onMeta); v.removeEventListener("resize", onMeta); v.srcObject = null; currentGeo.source = PLACEHOLDER_SIZE; };
  }, [stream]);

  // Input engine for the selected mode; Mouse and Touch never share handlers.
  useEffect(() => {
    const el = surfaceRef.current!;
    const host: EngineHost = {
      clock: browserClock,
      emit: bus.emit,
      container: () => geo.current.container,
      base: () => geo.current.base,
      rendered: () => applyView(geo.current.base, geo.current.view),
      view: () => geo.current.view,
      setView: (v) => { geo.current.view = v; applyLayout(); },
    };
    const engine = mode === "mouse" ? new MouseEngine(host, settingsRef.current, paintCursor) : new TouchEngine(host, paintCursor);
    engineRef.current = engine;
    onEngine?.(engine);
    if (engine instanceof MouseEngine) paintCursor(engine.cursor.nx, engine.cursor.ny);
    else if (cursorRef.current) cursorRef.current.style.opacity = "0";

    const pt = (e: PointerEvent) => ({ id: e.pointerId, x: e.clientX - geo.current.left, y: e.clientY - geo.current.top });
    const down = (e: PointerEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect(); geo.current.left = r.left; geo.current.top = r.top;
      try { el.setPointerCapture(e.pointerId); } catch { /* synthetic */ }
      engine.down(pt(e));
    };
    const move = (e: PointerEvent) => engine.move(pt(e));
    const up = (e: PointerEvent) => engine.up(pt(e));
    const cancel = () => engine.cancel();
    const block = (e: Event) => e.preventDefault();
    el.addEventListener("pointerdown", down);
    el.addEventListener("pointermove", move);
    el.addEventListener("pointerup", up);
    el.addEventListener("pointercancel", cancel);
    el.addEventListener("lostpointercapture", up as EventListener);
    el.addEventListener("contextmenu", block);
    el.addEventListener("gesturestart", block);
    el.addEventListener("wheel", block, { passive: false });
    return () => {
      el.removeEventListener("pointerdown", down);
      el.removeEventListener("pointermove", move);
      el.removeEventListener("pointerup", up);
      el.removeEventListener("pointercancel", cancel);
      el.removeEventListener("lostpointercapture", up as EventListener);
      el.removeEventListener("contextmenu", block);
      el.removeEventListener("gesturestart", block);
      el.removeEventListener("wheel", block);
      engine.dispose();
      engineRef.current = null;
      onEngine?.(null);
    };
  }, [mode, bus, onEngine]);

  return (
    <div ref={surfaceRef} data-testid="remote-surface" className="relative h-full w-full touch-none select-none overflow-hidden overscroll-none bg-screen" style={{ WebkitTouchCallout: "none" }}>
      <div ref={layerRef} className="absolute inset-0 origin-top-left will-change-transform">
        <div ref={frameRef} data-testid="remote-frame" className="absolute">
          <video ref={videoRef} className={stream ? "h-full w-full object-fill" : "hidden"} autoPlay playsInline muted />
          {!stream && (
            <div className="flex h-full w-full flex-col items-center justify-center gap-2 rounded-sm border border-dashed border-screen-foreground/20 p-4 text-center text-screen-foreground">
              <MonitorOff className="size-8 opacity-60" aria-hidden />
              <p className="font-display text-base font-semibold">No screen from {deviceName}</p>
              <p className="max-w-sm text-xs opacity-70">Live screen sharing isn't available yet — it arrives in the next phase. Your touches and key presses stay on this phone and are not sent to the laptop.</p>
            </div>
          )}
        </div>
      </div>
      <div ref={cursorRef} aria-hidden className="pointer-events-none absolute left-0 top-0 opacity-0">
        <div className="-ml-1.5 -mt-1.5 size-3 rounded-full border-2 border-screen-foreground bg-primary/70" />
      </div>
    </div>
  );
});
