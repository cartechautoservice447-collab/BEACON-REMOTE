import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Loader2, ShieldAlert } from "lucide-react";
import { toast } from "sonner";
import { AppShell, BackLink } from "@/components/app/AppShell";
import { FloatingToolbar } from "@/components/remote/FloatingToolbar";
import { KeyboardEditor } from "@/components/remote/KeyboardEditor";
import { MorePanel } from "@/components/remote/MorePanel";
import { RemoteScreen } from "@/components/remote/RemoteScreen";
import { RotateOverlay } from "@/components/remote/RotateOverlay";
import { VirtualKeyboard } from "@/components/remote/VirtualKeyboard";
import { Button } from "@/components/ui/button";
import { useKeyboardPrefs } from "@/hooks/use-keyboard-prefs";
import { usePortrait } from "@/hooks/use-orientation";
import { deviceQuery, type Device } from "@/lib/devices";
import { isDeviceOnline } from "@/lib/pairing";
import { KeyboardEngine } from "@/lib/remote/keyboard-engine";
import { activeLayout } from "@/lib/remote/keyboard-layout";
import { DEFAULT_MOUSE_SETTINGS, MouseEngine, type MouseSettings } from "@/lib/remote/mouse-engine";
import type { FitMode, InputMode } from "@/lib/remote/protocol";
import type { TouchEngine } from "@/lib/remote/touch-engine";
import { InputBus, NotConnectedTransport } from "@/lib/remote/transport";
import { enterFullscreenLandscape, exitFullscreen, fullscreenSupported, isFullscreen } from "@/lib/remote/viewport";

export const Route = createFileRoute("/_authenticated/remote/$deviceId")({
  head: () => ({
    meta: [
      { title: "Remote workspace — Beacon Remote" },
      { name: "description", content: "Remote desktop input workspace for your paired laptop." },
    ],
  }),
  component: RemoteWorkspaceRoute,
});

function RemoteWorkspaceRoute() {
  const { deviceId } = Route.useParams();
  const { user } = Route.useRouteContext();
  const navigate = useNavigate();
  const { data: device, isLoading, isError } = useQuery(deviceQuery(deviceId));

  if (isLoading) {
    return (
      <div className="flex h-dvh w-full items-center justify-center bg-screen text-screen-foreground">
        <div className="flex items-center gap-2 text-sm opacity-80">
          <Loader2 className="size-4 animate-spin" aria-hidden />
          Loading workspace…
        </div>
      </div>
    );
  }

  if (isError || !device) {
    return (
      <AppShell title="Workspace unavailable" back={<BackLink to="/devices" label="Devices" />}>
        <div className="rounded-2xl border bg-card p-6 text-center">
          <ShieldAlert className="mx-auto size-8 text-muted-foreground" aria-hidden />
          <h2 className="mt-3 text-lg font-semibold">This laptop isn't linked to your account</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            It may have been removed or you don't have access to it.
          </p>
          <Button asChild className="mt-5">
            <Link to="/devices">Back to devices</Link>
          </Button>
        </div>
      </AppShell>
    );
  }

  return (
    <RemoteWorkspace
      device={device}
      userId={user?.id}
      onDisconnectDone={() => navigate({ to: "/devices" })}
    />
  );
}

export function RemoteWorkspace({
  device,
  userId,
  onDisconnectDone,
  onBus,
}: {
  device: Device;
  userId: string | undefined;
  onDisconnectDone: () => void;
  onBus?: (bus: InputBus) => void;
}) {
  const workspaceRef = useRef<HTMLDivElement>(null);
  const bus = useMemo(() => new InputBus(new NotConnectedTransport()), []);
  const onBusRef = useRef(onBus);
  onBusRef.current = onBus;
  useEffect(() => {
    onBusRef.current?.(bus);
  }, [bus]);
  const keyboardEngine = useMemo(() => new KeyboardEngine(bus.emit), [bus]);
  const activeEngineRef = useRef<MouseEngine | TouchEngine | null>(null);
  const tornDownRef = useRef(false);

  const [prefs, setPrefs] = useKeyboardPrefs(userId);
  const portrait = usePortrait();

  const [mode, setMode] = useState<InputMode>("mouse");
  const [fit, setFit] = useState<FitMode>("fit");
  const [keyboardOpen, setKeyboardOpen] = useState(false);
  const [editorOpen, setEditorOpen] = useState(false);
  const [moreOpen, setMoreOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const [mouseSettings, setMouseSettings] = useState<MouseSettings>(DEFAULT_MOUSE_SETTINGS);
  const [portraitDismissed, setPortraitDismissed] = useState(false);
  const [fullscreenAvailable, setFullscreenAvailable] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [holding, setHolding] = useState(false);

  const releaseActiveInputs = useCallback(() => {
    keyboardEngine.releaseAll();
    activeEngineRef.current?.reset();
    bus.emit({ type: "input.reset" });
    setHolding(false);
  }, [bus, keyboardEngine]);

  const teardownWorkspace = useCallback(() => {
    if (tornDownRef.current) return;
    tornDownRef.current = true;
    releaseActiveInputs();
    bus.transport.close();
    void exitFullscreen(document);
  }, [bus, releaseActiveInputs]);

  // Release held keys and pointer buttons if the page is backgrounded or loses focus.
  useEffect(() => {
    const onVisibility = () => {
      if (!tornDownRef.current && document.visibilityState === "hidden") releaseActiveInputs();
    };
    const onBlur = () => {
      if (!tornDownRef.current) releaseActiveInputs();
    };
    document.addEventListener("visibilitychange", onVisibility);
    window.addEventListener("blur", onBlur);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      window.removeEventListener("blur", onBlur);
    };
  }, [releaseActiveInputs]);

  // Track real fullscreen state without interfering with external software keyboards.
  useEffect(() => {
    setFullscreenAvailable(fullscreenSupported(document));
    setFullscreen(isFullscreen(document));
    const sync = () => setFullscreen(isFullscreen(document));
    document.addEventListener("fullscreenchange", sync);
    document.addEventListener("webkitfullscreenchange", sync);
    return () => {
      document.removeEventListener("fullscreenchange", sync);
      document.removeEventListener("webkitfullscreenchange", sync);
    };
  }, []);

  // Idempotent cleanup on route unmount.
  useEffect(() => {
    tornDownRef.current = false;
    return () => {
      teardownWorkspace();
    };
  }, [teardownWorkspace]);

  const handleEngine = useCallback((engine: MouseEngine | TouchEngine | null) => {
    activeEngineRef.current = engine;
    setHolding(engine instanceof MouseEngine ? engine.isHolding : false);
  }, []);

  const handleMode = useCallback(
    (next: InputMode) => {
      if (next === mode) return;
      activeEngineRef.current?.reset();
      setHolding(false);
      setMode(next);
    },
    [mode],
  );

  const handleToggleKeyboard = useCallback(() => {
    setKeyboardOpen((open) => {
      if (open) keyboardEngine.releaseAll();
      return !open;
    });
  }, [keyboardEngine]);

  const handleToggleFit = useCallback(() => {
    setFit((f) => (f === "fit" ? "fill" : "fit"));
  }, []);

  const handleFullscreenLandscape = useCallback(async () => {
    const target = workspaceRef.current ?? document.documentElement;
    const res = await enterFullscreenLandscape(target, document);
    setFullscreen(isFullscreen(document));
    if (!res.fullscreen && !res.locked) {
      toast.info("Rotate your phone sideways or continue in portrait.");
    }
  }, []);

  const handleToggleFullscreen = useCallback(async () => {
    if (isFullscreen(document)) {
      await exitFullscreen(document);
      setFullscreen(false);
      return;
    }
    const target = workspaceRef.current ?? document.documentElement;
    const res = await enterFullscreenLandscape(target, document);
    setFullscreen(isFullscreen(document));
    if (!res.fullscreen) {
      toast.info("Full screen isn't available in this browser view.");
    }
  }, []);

  const handleMoreClick = useCallback((kind: "left" | "right" | "double" | "hold") => {
    const eng = activeEngineRef.current;
    if (!(eng instanceof MouseEngine)) return;
    if (kind === "left") eng.click("left");
    else if (kind === "right") eng.click("right");
    else if (kind === "double") eng.doubleClick();
    else eng.toggleHold();
    setHolding(eng.isHolding);
  }, []);

  const handleEditKeyboard = useCallback(() => {
    keyboardEngine.releaseAll();
    setMoreOpen(false);
    setEditorOpen(true);
  }, [keyboardEngine]);

  const handleDisconnect = useCallback(() => {
    if (tornDownRef.current) return;
    teardownWorkspace();
    onDisconnectDone();
  }, [onDisconnectDone, teardownWorkspace]);

  const layout = activeLayout(prefs);
  const deviceOnline = isDeviceOnline(device.last_heartbeat_at);

  return (
    <div
      ref={workspaceRef}
      data-testid="remote-workspace"
      className="relative flex h-dvh w-full flex-col overflow-hidden bg-screen text-screen-foreground"
    >
      <div className="relative min-h-0 flex-1">
        <RemoteScreen
          mode={mode}
          fit={fit}
          bus={bus}
          mouseSettings={mouseSettings}
          stream={null}
          deviceName={device.name}
          onEngine={handleEngine}
        />
        <FloatingToolbar
          mode={mode}
          onMode={handleMode}
          keyboardOpen={keyboardOpen}
          onKeyboard={handleToggleKeyboard}
          fit={fit}
          onFit={handleToggleFit}
          fullscreen={fullscreen}
          fullscreenAvailable={fullscreenAvailable}
          onFullscreen={() => void handleToggleFullscreen()}
          onMore={() => setMoreOpen(true)}
          onDisconnect={handleDisconnect}
          collapsed={collapsed}
          onCollapse={() => setCollapsed((c) => !c)}
          connection={bus.transport.state}
        />
      </div>

      {keyboardOpen && (
        <VirtualKeyboard
          layout={layout}
          engine={keyboardEngine}
          compact={layout.base === "compact"}
        />
      )}

      <MorePanel
        open={moreOpen}
        onOpenChange={setMoreOpen}
        mode={mode}
        settings={mouseSettings}
        onSettings={setMouseSettings}
        prefs={prefs}
        onPrefs={setPrefs}
        onEditKeyboard={handleEditKeyboard}
        onClick={handleMoreClick}
        holding={holding}
        deviceName={device.name}
        deviceOnline={deviceOnline}
      />

      <KeyboardEditor open={editorOpen} onOpenChange={setEditorOpen} prefs={prefs} onChange={setPrefs} />

      {portrait && !portraitDismissed && (
        <RotateOverlay
          onFullscreen={() => void handleFullscreenLandscape()}
          onDismiss={() => setPortraitDismissed(true)}
        />
      )}
    </div>
  );
}
