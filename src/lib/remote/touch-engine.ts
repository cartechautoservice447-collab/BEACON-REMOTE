import { FrameBatcher } from "./frame-batcher";
import { classifyTwoFinger, distance, midpoint, LONG_PRESS_MS, TAP_SLOP_PX, type TwoFingerKind } from "./gestures";
import { clampPan, MAX_ZOOM, toRemote, type Rect, type ViewTransform } from "./coords";
import type { EngineHost, InputEngine } from "./engine-host";
import type { PointerSample } from "./protocol";

type Track = { start: PointerSample; last: PointerSample };

/**
 * Direct touch: the finger is the cursor (1:1 mapping through letterboxing and zoom).
 * Tap = click at point, drag = left-button drag, long press = right click,
 * two fingers = pinch-zoom (local view) or scroll, locked once decided.
 */
export class TouchEngine implements InputEngine {
  scrollSpeed = 1;
  private tracks = new Map<number, Track>();
  private phase: "idle" | "one" | "dragging" | "two" | "done" = "idle";
  private longPress: number | null = null;
  private kind: TwoFingerKind = "undecided";
  private twoStart = { dist: 0, mid: { x: 0, y: 0 }, view: { zoom: 1, panX: 0, panY: 0 } as ViewTransform };
  private lastMid = { x: 0, y: 0 };
  private pendingAbs: { nx: number; ny: number } | null = null;
  private wdx = 0; private wdy = 0;
  private batcher: FrameBatcher;

  constructor(private host: EngineHost, private onPoint?: (nx: number, ny: number) => void) {
    this.batcher = new FrameBatcher(host.clock, () => this.flush());
  }

  private map(p: { x: number; y: number }) { return toRemote(p.x, p.y, this.host.rendered()); }
  private baseRect(): Rect {
    if (this.host.base) return this.host.base();
    const r = this.host.rendered();
    const v = this.host.view();
    return { x: (r.x - v.panX) / v.zoom, y: (r.y - v.panY) / v.zoom, w: r.w / v.zoom, h: r.h / v.zoom };
  }

  down(p: PointerSample) {
    this.tracks.set(p.id, { start: p, last: p });
    if (this.tracks.size === 1 && this.phase === "idle") {
      this.phase = "one";
      this.longPress = this.host.clock.setTimeout(() => {
        this.longPress = null;
        if (this.phase !== "one") return;
        const m = this.map(p);
        if (!m.inside) return;
        this.host.emit({ type: "pointer.move.abs", x: m.nx, y: m.ny });
        this.host.emit({ type: "pointer.button", button: "right", action: "down", x: m.nx, y: m.ny });
        this.host.emit({ type: "pointer.button", button: "right", action: "up", x: m.nx, y: m.ny });
        this.phase = "done";
      }, LONG_PRESS_MS);
    } else if (this.tracks.size === 2) {
      this.clearLongPress();
      if (this.phase === "dragging") this.endDrag();
      const [a, b] = [...this.tracks.values()] as [Track, Track];
      this.phase = "two";
      this.kind = "undecided";
      this.twoStart = { dist: distance(a.last, b.last), mid: midpoint(a.last, b.last), view: this.host.view() };
      this.lastMid = this.twoStart.mid;
    } else if (this.tracks.size > 2) {
      this.phase = "done";
    }
  }

  move(p: PointerSample) {
    const t = this.tracks.get(p.id);
    if (!t) return;
    t.last = p;
    if (this.phase === "one" && distance(t.start, p) > TAP_SLOP_PX) {
      this.clearLongPress();
      const s = this.map(t.start);
      if (!s.inside) { this.phase = "done"; return; }
      this.host.emit({ type: "pointer.move.abs", x: s.nx, y: s.ny });
      this.host.emit({ type: "pointer.button", button: "left", action: "down", x: s.nx, y: s.ny });
      this.lastDragPoint = { nx: s.nx, ny: s.ny };
      this.phase = "dragging";
    }
    if (this.phase === "dragging") {
      const m = this.map(p);
      this.pendingAbs = { nx: m.nx, ny: m.ny };
      this.batcher.schedule();
    } else if (this.phase === "two" && this.tracks.size === 2) {
      const [a, b] = [...this.tracks.values()] as [typeof t, typeof t];
      const dist = distance(a.last, b.last), mid = midpoint(a.last, b.last);
      if (this.kind === "undecided") this.kind = classifyTwoFinger(this.twoStart.dist, dist, this.twoStart.mid, mid);
      if (this.kind === "pinch") {
        const c = this.host.container();
        const startView = this.twoStart.view;
        const zoom = Math.min(MAX_ZOOM, Math.max(1, startView.zoom * (dist / Math.max(1, this.twoStart.dist))));
        const k = zoom / startView.zoom;
        const panX = mid.x - (this.twoStart.mid.x - startView.panX) * k;
        const panY = mid.y - (this.twoStart.mid.y - startView.panY) * k;
        this.host.setView(clampPan({ zoom, panX, panY }, c, this.baseRect()));
      } else if (this.kind === "scroll") {
        this.wdx += mid.x - this.lastMid.x; this.wdy += mid.y - this.lastMid.y;
        this.batcher.schedule();
      }
      this.lastMid = mid;
    }
  }

  up(p: PointerSample) {
    const t = this.tracks.get(p.id);
    if (!t) return;
    this.tracks.delete(p.id);
    if (this.phase === "one") {
      this.clearLongPress();
      const m = this.map(t.start);
      if (m.inside) {
        this.host.emit({ type: "pointer.move.abs", x: m.nx, y: m.ny });
        this.host.emit({ type: "pointer.button", button: "left", action: "down", x: m.nx, y: m.ny });
        this.host.emit({ type: "pointer.button", button: "left", action: "up", x: m.nx, y: m.ny });
        this.onPoint?.(m.nx, m.ny);
      }
      this.phase = "done";
    } else if (this.phase === "dragging") {
      this.endDrag();
      this.phase = "done";
    }
    if (this.tracks.size === 0) { this.batcher.flushNow(); this.phase = "idle"; this.kind = "undecided"; }
  }

  cancel() {
    this.clearLongPress();
    if (this.phase === "dragging") this.endDrag();
    this.tracks.clear();
    this.batcher.flushNow();
    this.phase = "idle";
  }
  reset() { this.cancel(); }
  dispose() { this.cancel(); this.batcher.dispose(); }

  private endDrag() {
    this.batcher.flushNow();
    const last = this.lastDragPoint ?? { nx: 0, ny: 0 };
    this.host.emit({ type: "pointer.button", button: "left", action: "up", x: last.nx, y: last.ny });
    this.phase = "done";
  }
  private lastDragPoint: { nx: number; ny: number } | null = null;
  private clearLongPress() { if (this.longPress !== null) { this.host.clock.clearTimeout(this.longPress); this.longPress = null; } }

  private flush() {
    if (this.pendingAbs) {
      const { nx, ny } = this.pendingAbs;
      this.pendingAbs = null;
      this.lastDragPoint = { nx, ny };
      this.host.emit({ type: "pointer.move.abs", x: nx, y: ny });
      this.onPoint?.(nx, ny);
    }
    if (this.wdx !== 0 || this.wdy !== 0) {
      this.host.emit({ type: "pointer.wheel", dx: -this.wdx * this.scrollSpeed, dy: -this.wdy * this.scrollSpeed });
      this.wdx = 0; this.wdy = 0;
    }
  }
}
