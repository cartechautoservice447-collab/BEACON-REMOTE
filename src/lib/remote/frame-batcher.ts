import type { Clock } from "./protocol";

/** Coalesces high-frequency updates into at most one flush per animation frame. */
export class FrameBatcher {
  private id: number | null = null;
  constructor(private clock: Clock, private flushFn: () => void) {}
  schedule() {
    if (this.id !== null) return;
    this.id = this.clock.requestFrame(() => { this.id = null; this.flushFn(); });
  }
  /** Flush synchronously (used before button events so ordering is preserved). */
  flushNow() {
    if (this.id !== null) { this.clock.cancelFrame(this.id); this.id = null; }
    this.flushFn();
  }
  dispose() { if (this.id !== null) this.clock.cancelFrame(this.id); this.id = null; }
}
