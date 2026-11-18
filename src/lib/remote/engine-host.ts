import type { Clock, RemoteInputEvent } from "./protocol";
import type { Rect, Size, ViewTransform } from "./coords";

/** What an input engine needs from the screen it's attached to. Kept UI-agnostic. */
export interface EngineHost {
  clock: Clock;
  emit(e: RemoteInputEvent): void;
  /** Container size in CSS px. */
  container(): Size;
  /** Unzoomed fitted desktop rect inside the container. */
  base?(): Rect;
  /** Remote frame rect inside the container, including local zoom/pan. */
  rendered(): Rect;
  view(): ViewTransform;
  setView(v: ViewTransform): void;
}

export interface InputEngine {
  down(p: { id: number; x: number; y: number }): void;
  move(p: { id: number; x: number; y: number }): void;
  up(p: { id: number; x: number; y: number }): void;
  cancel(): void;
  /** Releases any held button and clears pending work. */
  reset(): void;
  dispose(): void;
}
