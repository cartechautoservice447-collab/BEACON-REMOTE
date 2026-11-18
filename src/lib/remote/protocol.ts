/**
 * Remote input protocol. Pointer coordinates are normalized (0..1) against the
 * remote desktop so they stay valid whatever the stream resolution is.
 * Nothing here is transmitted until a real transport (Phase 3) is connected.
 */
export type MouseButton = "left" | "right" | "middle";

export type RemoteInputEvent =
  | { type: "pointer.move.abs"; x: number; y: number }
  | { type: "pointer.button"; button: MouseButton; action: "down" | "up"; x: number; y: number }
  | { type: "pointer.wheel"; dx: number; dy: number }
  | { type: "key"; code: string; action: "down" | "up" }
  | { type: "input.reset" };

export type InputMode = "mouse" | "touch";
export type FitMode = "fit" | "fill";

/** Optional timing source so engines can be driven deterministically in tests. */
export interface Clock {
  now(): number;
  setTimeout(fn: () => void, ms: number): number;
  clearTimeout(id: number): void;
  requestFrame(fn: () => void): number;
  cancelFrame(id: number): void;
}

export const browserClock: Clock = {
  now: () => performance.now(),
  setTimeout: (fn, ms) => window.setTimeout(fn, ms),
  clearTimeout: (id) => window.clearTimeout(id),
  requestFrame: (fn) => window.requestAnimationFrame(fn),
  cancelFrame: (id) => window.cancelAnimationFrame(id),
};

export type PointerSample = { id: number; x: number; y: number };
