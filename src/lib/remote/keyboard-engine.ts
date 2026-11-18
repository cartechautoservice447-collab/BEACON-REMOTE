import type { RemoteInputEvent } from "./protocol";
import { KEY_BY_ID } from "./keys";

export type KeyboardSnapshot = { modifiers: Record<string, "off" | "once" | "locked">; capsOn: boolean; pressed: string[] };

/**
 * Virtual keyboard state machine with real key-down/key-up semantics.
 * Modifiers: tap = held for the next key (one-shot), tap again = locked, third tap = released.
 * releaseAll() guarantees nothing stays stuck on the remote side.
 */
export class KeyboardEngine {
  private mods = new Map<string, "once" | "locked">();
  private pressed = new Set<string>();
  private capsOn = false;
  private listeners = new Set<() => void>();
  private snap: KeyboardSnapshot = { modifiers: {}, capsOn: false, pressed: [] };

  constructor(private emit: (e: RemoteInputEvent) => void) {}

  subscribe = (l: () => void) => { this.listeners.add(l); return () => { this.listeners.delete(l); }; };
  getSnapshot = () => this.snap;

  /** Pointer went down on a key. */
  press(id: string) {
    const def = KEY_BY_ID[id];
    if (!def) return;
    if (def.kind === "modifier") {
      const s = this.mods.get(id);
      if (!s) { this.mods.set(id, "once"); this.send(def.code, "down"); }
      else if (s === "once") this.mods.set(id, "locked");
      else { this.mods.delete(id); this.send(def.code, "up"); }
    } else if (def.kind === "lock") {
      this.send(def.code, "down"); this.send(def.code, "up"); this.capsOn = !this.capsOn;
    } else {
      if (this.pressed.has(id)) return;
      this.pressed.add(id); this.send(def.code, "down");
    }
    this.changed();
  }

  /** Pointer released from a key. */
  release(id: string) {
    const def = KEY_BY_ID[id];
    if (!def || def.kind !== "key" || !this.pressed.has(id)) return;
    this.pressed.delete(id);
    this.send(def.code, "up");
    // One-shot modifiers end once the last normal key is released.
    if (this.pressed.size === 0) for (const [m, s] of this.mods) if (s === "once") { this.mods.delete(m); this.send(KEY_BY_ID[m]!.code, "up"); }
    this.changed();
  }

  /** Release every held key and modifier (keyboard closed, disconnect, app hidden). */
  releaseAll() {
    if (this.pressed.size === 0 && this.mods.size === 0) return;
    for (const id of this.pressed) this.send(KEY_BY_ID[id]!.code, "up");
    for (const m of this.mods.keys()) this.send(KEY_BY_ID[m]!.code, "up");
    this.pressed.clear(); this.mods.clear();
    this.changed();
  }

  get heldCodes() { return [...this.pressed, ...this.mods.keys()].map((id) => KEY_BY_ID[id]!.code); }

  private send(code: string, action: "down" | "up") { this.emit({ type: "key", code, action }); }
  private changed() {
    this.snap = { modifiers: Object.fromEntries([...this.mods]), capsOn: this.capsOn, pressed: [...this.pressed] };
    for (const l of this.listeners) l();
  }
}
