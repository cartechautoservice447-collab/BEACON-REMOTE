import type { PointerSample } from "./protocol";

export const TAP_SLOP_PX = 10;
export const TAP_MAX_MS = 250;
export const LONG_PRESS_MS = 500;
export const PINCH_THRESHOLD_PX = 14;
export const SCROLL_THRESHOLD_PX = 8;

export const distance = (a: PointerSample, b: PointerSample) => Math.hypot(a.x - b.x, a.y - b.y);
export const midpoint = (a: PointerSample, b: PointerSample) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

export type TwoFingerKind = "undecided" | "pinch" | "scroll";

/** Decides once whether a two-finger gesture is a pinch or a scroll; it then stays locked. */
export function classifyTwoFinger(startDist: number, dist: number, startMid: { x: number; y: number }, mid: { x: number; y: number }): TwoFingerKind {
  const spread = Math.abs(dist - startDist);
  const travel = Math.hypot(mid.x - startMid.x, mid.y - startMid.y);
  if (spread >= PINCH_THRESHOLD_PX && spread > travel) return "pinch";
  if (travel >= SCROLL_THRESHOLD_PX) return "scroll";
  return "undecided";
}
