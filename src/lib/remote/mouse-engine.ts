import { FrameBatcher } from "./frame-batcher";
import { distance, midpoint, LONG_PRESS_MS, SCROLL_THRESHOLD_PX, TAP_MAX_MS, TAP_SLOP_PX } from "./gestures";
import type { EngineHost, InputEngine } from "./engine-host";
import type { MouseButton, PointerSample } from "./protocol";

export type MouseSettings = { sensitivity: number; scrollSpeed: number; acceleration: boolean };
export const DEFAULT_MOUSE_SETTINGS: MouseSettings = { sensitivity: 1.4, scrollSpeed: 1, acceleration: true };

type Track = { start: PointerSample; last: PointerSample };

/**
 * Trackpad-style relative mouse. One finger moves the cursor, tap = left click,
 * two-finger tap = right click, press-and-hold = button held (drag), two fingers = scroll.
 */
export class MouseEngine implements InputEngine {
  cursor = { nx: 0.5, ny: 0.5 };
  settings: MouseSettings;
  private tracks = new Map<number, Track>();
  private maxFingers = 0;
  private startedAt = 0;
  private moved = false;
  private scrolled = false;
  private holding = false;
  private manualHold = false;
  private longPressed = false;
  private longPress: number | null = null;
  private pdx = 0; private pdy = 0; private wdx = 0; private wdy = 0;
  private batcher: FrameBatcher;

  constructor(private host: EngineHost, settings: Partial<MouseSettings> = {}, private onCursor?: (nx: number, ny: number) => void) {
    this.settings = { ...DEFAULT_MOUSE_SETTINGS, ...settings };
    this.batcher = new FrameBatcher(host.clock, () => this.flush());
  }

  down(p: PointerSample) {
    if (this.tracks.size === 0) { this.maxFingers = 0; this.startedAt = this.host.clock.now(); this.moved = false; this.scrolled = false; this.longPressed = false; }
    this.tracks.set(p.id, { start: p, last: p });
    this.maxFingers = Math.max(this.maxFingers, this.tracks.size);
    this.clearLongPress();
    if (this.tracks.size === 1) {
      this.longPress = this.host.clock.setTimeout(() => {
        this.longPress = null;
        if (!this.moved && this.tracks.size === 1 && !this.holding) {
          this.longPressed = true;
          this.press("left");
        }
      }, LONG_PRESS_MS);
    } else if (this.tracks.size > 1 && this.holding && !this.manualHold) {
      this.batcher.flushNow();
      this.release("left");
    }
  }

  move(p: PointerSample) {
    const t = this.tracks.get(p.id);
    if (!t) return;
    const dx = p.x - t.last.x, dy = p.y - t.last.y;
    t.last = p;
    if (this.tracks.size === 1 && this.maxFingers === 1) {
      if (!this.moved && distance(t.start, p) > TAP_SLOP_PX) { this.moved = true; this.clearLongPress(); }
      if (this.moved || this.holding) { this.pdx += dx; this.pdy += dy; this.batcher.schedule(); }
    } else if (this.tracks.size === 2) {
      const [a, b] = [...this.tracks.values()] as [typeof t, typeof t];
      const travel = distance(midpoint(a.start, b.start) as PointerSample, midpoint(a.last, b.last) as PointerSample);
      if (!this.scrolled && travel >= SCROLL_THRESHOLD_PX) this.scrolled = true;
      if (this.scrolled) { this.wdx += dx / 2; this.wdy += dy / 2; this.batcher.schedule(); }
    }
  }

  up(p: PointerSample) {
    if (!this.tracks.delete(p.id)) return;
    if (this.tracks.size > 0) return;
    this.clearLongPress();
    this.batcher.flushNow();
    const quick = this.host.clock.now() - this.startedAt <= TAP_MAX_MS * (this.maxFingers > 1 ? 1.6 : 1);
    if (this.holding && !this.manualHold) this.release("left");
    else if (!this.longPressed && this.maxFingers === 1 && !this.moved && quick) this.click("left");
    else if (!this.longPressed && this.maxFingers === 2 && !this.scrolled && quick) this.click("right");
  }

  cancel() { this.tracks.clear(); this.longPressed = false; this.clearLongPress(); this.batcher.flushNow(); if (this.holding) this.release("left"); }

  /** Toolbar helpers. Button down/up are emitted immediately (never debounced). */
  click(button: MouseButton) { this.batcher.flushNow(); this.press(button); this.release(button); }
  doubleClick() { this.click("left"); this.click("left"); }
  toggleHold() { if (this.holding) { this.manualHold = false; this.release("left"); } else { this.manualHold = true; this.press("left"); } }
  get isHolding() { return this.holding; }

  reset() { this.manualHold = false; this.cancel(); }
  dispose() { this.reset(); this.batcher.dispose(); }

  private press(button: MouseButton) {
    if (button === "left") this.holding = true;
    this.host.emit({ type: "pointer.button", button, action: "down", x: this.cursor.nx, y: this.cursor.ny });
  }
  private release(button: MouseButton) {
    if (button === "left") this.holding = false;
    this.host.emit({ type: "pointer.button", button, action: "up", x: this.cursor.nx, y: this.cursor.ny });
  }
  private clearLongPress() { if (this.longPress !== null) { this.host.clock.clearTimeout(this.longPress); this.longPress = null; } }

  private flush() {
    if (this.pdx !== 0 || this.pdy !== 0) {
      const r = this.host.rendered();
      const mag = Math.hypot(this.pdx, this.pdy);
      // Mild acceleration: slow strokes stay precise, fast strokes cover the screen.
      const gain = this.settings.sensitivity * (this.settings.acceleration ? 1 + Math.min(mag, 40) / 40 : 1);
      const w = Math.max(1, r.w), h = Math.max(1, r.h);
      this.cursor = { nx: clamp01(this.cursor.nx + (this.pdx * gain) / w), ny: clamp01(this.cursor.ny + (this.pdy * gain) / h) };
      this.pdx = 0; this.pdy = 0;
      this.host.emit({ type: "pointer.move.abs", x: this.cursor.nx, y: this.cursor.ny });
      this.onCursor?.(this.cursor.nx, this.cursor.ny);
    }
    if (this.wdx !== 0 || this.wdy !== 0) {
      const s = this.settings.scrollSpeed;
      this.host.emit({ type: "pointer.wheel", dx: -this.wdx * s, dy: -this.wdy * s });
      this.wdx = 0; this.wdy = 0;
    }
  }
}

const clamp01 = (n: number) => Math.min(1, Math.max(0, n));
