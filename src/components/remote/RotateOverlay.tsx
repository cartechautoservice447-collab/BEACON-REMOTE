import { Smartphone } from "lucide-react";
import { Button } from "@/components/ui/button";

export function RotateOverlay({ onFullscreen, onDismiss }: { onFullscreen: () => void; onDismiss: () => void }) {
  return (
    <div role="dialog" aria-label="Rotate your phone" className="absolute inset-0 z-50 flex flex-col items-center justify-center gap-5 bg-background/95 p-8 text-center" style={{ paddingTop: "max(2rem, env(safe-area-inset-top))", paddingBottom: "max(2rem, env(safe-area-inset-bottom))" }}>
      <div className="grid size-20 place-items-center rounded-2xl bg-accent text-accent-foreground">
        <Smartphone className="size-10 animate-[spin_2.4s_ease-in-out_infinite] [animation-iteration-count:1]" style={{ transform: "rotate(90deg)" }} aria-hidden />
      </div>
      <div>
        <h2 className="font-display text-xl font-semibold">Turn your phone sideways</h2>
        <p className="mt-2 max-w-xs text-sm text-muted-foreground">The remote screen is designed for landscape so your laptop's desktop fits.</p>
      </div>
      <div className="flex w-full max-w-xs flex-col gap-2">
        <Button className="h-11" onClick={onFullscreen}>Go full screen & rotate</Button>
        <Button variant="ghost" className="h-11" onClick={onDismiss}>Continue in portrait</Button>
      </div>
    </div>
  );
}
