import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Slider } from "@/components/ui/slider";
import { Switch } from "@/components/ui/switch";
import type { MouseSettings } from "@/lib/remote/mouse-engine";
import type { KeyboardPrefs } from "@/lib/remote/keyboard-layout";
import type { InputMode } from "@/lib/remote/protocol";

type Props = {
  open: boolean; onOpenChange: (o: boolean) => void;
  mode: InputMode;
  settings: MouseSettings; onSettings: (s: MouseSettings) => void;
  prefs: KeyboardPrefs; onPrefs: (p: KeyboardPrefs) => void;
  onEditKeyboard: () => void;
  onClick: (k: "left" | "right" | "double" | "hold") => void;
  holding: boolean;
  deviceName: string; deviceOnline: boolean;
};

export function MorePanel(p: Props) {
  return (
    <Sheet open={p.open} onOpenChange={p.onOpenChange}>
      <SheetContent side="right" className="w-80 overflow-y-auto">
        <SheetHeader>
          <SheetTitle>Controls</SheetTitle>
          <SheetDescription>{p.deviceName} · laptop app {p.deviceOnline ? "online" : "offline"} · screen not connected</SheetDescription>
        </SheetHeader>
        <div className="space-y-6 px-4 pb-6">
          {p.mode === "mouse" && (
            <section className="space-y-2">
              <p className="text-sm font-medium">Mouse buttons</p>
              <div className="grid grid-cols-2 gap-2">
                <Button variant="secondary" onClick={() => p.onClick("left")}>Left click</Button>
                <Button variant="secondary" onClick={() => p.onClick("right")}>Right click</Button>
                <Button variant="secondary" onClick={() => p.onClick("double")}>Double click</Button>
                <Button variant={p.holding ? "default" : "secondary"} aria-pressed={p.holding} onClick={() => p.onClick("hold")}>{p.holding ? "Release hold" : "Click & hold"}</Button>
              </div>
            </section>
          )}
          <section className="space-y-3">
            <div className="flex justify-between"><Label>Pointer sensitivity</Label><span className="text-xs text-muted-foreground">{p.settings.sensitivity.toFixed(1)}×</span></div>
            <Slider min={0.4} max={4} step={0.1} value={[p.settings.sensitivity]} onValueChange={([v]) => p.onSettings({ ...p.settings, sensitivity: v ?? 1 })} aria-label="Pointer sensitivity" />
            <div className="flex justify-between"><Label>Scroll speed</Label><span className="text-xs text-muted-foreground">{p.settings.scrollSpeed.toFixed(1)}×</span></div>
            <Slider min={0.2} max={3} step={0.1} value={[p.settings.scrollSpeed]} onValueChange={([v]) => p.onSettings({ ...p.settings, scrollSpeed: v ?? 1 })} aria-label="Scroll speed" />
            <div className="flex items-center justify-between"><Label htmlFor="accel">Pointer acceleration</Label><Switch id="accel" checked={p.settings.acceleration} onCheckedChange={(v) => p.onSettings({ ...p.settings, acceleration: v })} /></div>
          </section>
          <section className="space-y-2">
            <p className="text-sm font-medium">Keyboard</p>
            <div className="grid grid-cols-1 gap-2">
              {p.prefs.layouts.map((l) => (
                <Button key={l.id} variant={p.prefs.activeId === l.id ? "default" : "secondary"} onClick={() => p.onPrefs({ ...p.prefs, activeId: l.id })}>{l.name}</Button>
              ))}
              <Button variant="outline" onClick={p.onEditKeyboard}>Customize keyboard…</Button>
            </div>
          </section>
          <section className="rounded-lg border p-3 text-xs text-muted-foreground">
            <p className="font-medium text-foreground">Gestures</p>
            {p.mode === "mouse" ? (
              <p className="mt-1">Slide one finger to move the pointer · tap to click · two-finger tap for right click · press and hold, then slide to drag · two fingers to scroll.</p>
            ) : (
              <p className="mt-1">Tap where you want to click · drag to drag · long press for right click · pinch to zoom · two fingers to scroll.</p>
            )}
          </section>
        </div>
      </SheetContent>
    </Sheet>
  );
}
