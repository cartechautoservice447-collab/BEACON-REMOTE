import { describe, expect, test } from "vitest";
import { applyView, clampPan, contentRect, fromRemote, toDesktopPixels, toRemote, zoomAt, IDENTITY_VIEW, type Rect, type Size, type ViewTransform } from "./coords";
import { MouseEngine } from "./mouse-engine";
import { TouchEngine } from "./touch-engine";
import { KeyboardEngine } from "./keyboard-engine";
import { addKey, availableKeys, createLayout, defaultFull, defaultPrefs, deleteLayout, loadPrefs, moveKey, parsePrefs, removeKey, restoreDefault, savePrefs } from "./keyboard-layout";
import { InputBus, NotConnectedTransport } from "./transport";
import { fullscreenSupported, isPortrait } from "./viewport";
import type { Clock, RemoteInputEvent } from "./protocol";

function fakeClock() {
  let t = 0, nextId = 1;
  const timers = new Map<number, { at: number; fn: () => void }>();
  const frames = new Map<number, () => void>();
  const clock: Clock = {
    now: () => t,
    setTimeout: (fn, ms) => { const id = nextId++; timers.set(id, { at: t + ms, fn }); return id; },
    clearTimeout: (id) => { timers.delete(id); },
    requestFrame: (fn) => { const id = nextId++; frames.set(id, fn); return id; },
    cancelFrame: (id) => { frames.delete(id); },
  };
  return {
    clock, frames, timers,
    advance(ms: number) { t += ms; for (const [id, x] of [...timers]) if (x.at <= t) { timers.delete(id); x.fn(); } },
    frame() { const fs = [...frames.values()]; frames.clear(); fs.forEach((f) => f()); },
  };
}

function host(rect: Rect = { x: 0, y: 0, w: 1000, h: 500 }, container: Size = { w: 1000, h: 500 }) {
  const fc = fakeClock();
  const events: RemoteInputEvent[] = [];
  let view: ViewTransform = IDENTITY_VIEW;
  const h = {
    clock: fc.clock,
    emit: (e: RemoteInputEvent) => events.push(e),
    container: () => container,
    base: () => rect,
    rendered: () => applyView(rect, view),
    view: () => view,
    setView: (v: ViewTransform) => { view = v; },
  };
  return { h, fc, events, getView: () => view };
}
const types = (es: RemoteInputEvent[]) => es.map((e) => e.type === "pointer.button" ? `${e.button}.${e.action}` : e.type === "key" ? `${e.code}.${e.action}` : e.type);

describe("coordinate mapping", () => {
  test("16:9 desktop in a 4:3 container is letterboxed top and bottom", () => {
    expect(contentRect({ w: 800, h: 600 }, { w: 1920, h: 1080 }, "fit")).toEqual({ x: 0, y: 75, w: 800, h: 450 });
  });
  test("fill mode covers the container without distortion", () => {
    const r = contentRect({ w: 800, h: 600 }, { w: 1920, h: 1080 }, "fill");
    expect(r.h).toBe(600); expect(r.w / r.h).toBeCloseTo(1920 / 1080, 6);
  });
  test("tap on the letterbox bar is outside the desktop", () => {
    expect(toRemote(400, 40, { x: 0, y: 75, w: 800, h: 450 }).inside).toBe(false);
  });
  test("center of a letterboxed frame maps to desktop center pixel", () => {
    const m = toRemote(400, 300, { x: 0, y: 75, w: 800, h: 450 });
    expect(toDesktopPixels(m.nx, m.ny, { w: 1920, h: 1080 })).toEqual({ x: 960, y: 540 });
  });
  test("pinch-zoom keeps the focal point fixed and mapping follows the zoom", () => {
    const base = { x: 0, y: 0, w: 1000, h: 500 };
    const before = toRemote(250, 125, base);
    const v = zoomAt(IDENTITY_VIEW, 2, 250, 125, { w: 1000, h: 500 });
    const after = toRemote(250, 125, applyView(base, v));
    expect(after.nx).toBeCloseTo(before.nx, 6); expect(after.ny).toBeCloseTo(before.ny, 6);
  });
  test("zoom is clamped between 1x and 4x", () => {
    expect(zoomAt(IDENTITY_VIEW, 10, 0, 0, { w: 100, h: 100 }).zoom).toBe(4);
    expect(zoomAt(IDENTITY_VIEW, 0.2, 0, 0, { w: 100, h: 100 }).zoom).toBe(1);
  });
  test("base-aware clampPan centers 16:9 desktop in portrait container and clamps edges when zoomed", () => {
    const container = { w: 390, h: 844 };
    const base = contentRect(container, { w: 1920, h: 1080 }, "fit");
    // At 2x zoom, width (780) exceeds 390 while height (438.75) is smaller than 844 -> centered vertically.
    const v2 = clampPan({ zoom: 2, panX: -100, panY: 999 }, container, base);
    const r2 = applyView(base, v2);
    expect(r2.x).toBe(-100);
    expect(r2.y).toBeCloseTo((844 - base.h * 2) / 2, 6);
    // At 4x zoom, height (877.5) exceeds 844 -> top and bottom edges clamp flush to container bounds.
    const topClamped = applyView(base, clampPan({ zoom: 4, panX: 500, panY: 500 }, container, base));
    expect(topClamped.x).toBeCloseTo(0, 6);
    expect(topClamped.y).toBeCloseTo(0, 6);
    const bottomClamped = applyView(base, clampPan({ zoom: 4, panX: -5000, panY: -5000 }, container, base));
    expect(bottomClamped.x + bottomClamped.w).toBeCloseTo(390, 6);
    expect(bottomClamped.y + bottomClamped.h).toBeCloseTo(844, 6);
  });
  test("base-aware clampPan handles pillarboxed and ultrawide aspect ratios", () => {
    const container = { w: 1000, h: 500 };
    const pillar = contentRect(container, { w: 1200, h: 1000 }, "fit"); // 600x500 centered at x=200
    const v15 = clampPan({ zoom: 1.5, panX: 300, panY: 100 }, container, pillar);
    const r15 = applyView(pillar, v15);
    expect(r15.x).toBeCloseTo((1000 - pillar.w * 1.5) / 2, 6);
    expect(r15.y).toBeCloseTo(0, 6);

    const v2 = clampPan({ zoom: 2, panX: 0, panY: 0 }, container, pillar);
    const r2 = applyView(pillar, v2);
    expect(r2.x).toBeCloseTo(0, 6); // left desktop edge flush with container left edge

    const wideContainer = { w: 1280, h: 720 };
    const ultrawide = contentRect(wideContainer, { w: 2560, h: 1080 }, "fit"); // 1280x540 at y=90
    const uwRightBottom = applyView(ultrawide, clampPan({ zoom: 2, panX: -9999, panY: -9999 }, wideContainer, ultrawide));
    expect(uwRightBottom.x + uwRightBottom.w).toBeCloseTo(1280, 6);
    expect(uwRightBottom.y + uwRightBottom.h).toBeCloseTo(720, 6);
  });
});

describe("mouse mode", () => {
  test("tap produces left down then up", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h);
    m.down({ id: 1, x: 10, y: 10 }); fc.advance(100); m.up({ id: 1, x: 10, y: 10 });
    expect(types(events)).toEqual(["left.down", "left.up"]);
  });
  test("moves are coalesced into one event per frame", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h, { acceleration: false, sensitivity: 1 });
    m.down({ id: 1, x: 0, y: 0 });
    for (let i = 1; i <= 20; i++) m.move({ id: 1, x: i * 5, y: 0 });
    expect(events.length).toBe(0);
    fc.frame();
    expect(types(events)).toEqual(["pointer.move.abs"]);
    // The first 10px are the tap slop and are not treated as movement.
    expect(m.cursor.nx).toBeCloseTo(0.5 + 90 / 1000, 6);
  });
  test("sensitivity scales relative movement", () => {
    const { h, fc } = host(); const m = new MouseEngine(h, { acceleration: false, sensitivity: 2 });
    m.down({ id: 1, x: 0, y: 0 }); m.move({ id: 1, x: 50, y: 0 }); fc.frame();
    expect(m.cursor.nx).toBeCloseTo(0.6, 6);
  });
  test("long press then move drags: button down, moves, button up in order", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h);
    m.down({ id: 1, x: 0, y: 0 }); fc.advance(600);
    m.move({ id: 1, x: 40, y: 0 }); fc.frame();
    m.move({ id: 1, x: 80, y: 0 });
    m.up({ id: 1, x: 80, y: 0 });
    expect(types(events)).toEqual(["left.down", "pointer.move.abs", "pointer.move.abs", "left.up"]);
  });
  test("two-finger tap is a right click", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h);
    m.down({ id: 1, x: 0, y: 0 }); m.down({ id: 2, x: 50, y: 0 }); fc.advance(120);
    m.up({ id: 1, x: 0, y: 0 }); m.up({ id: 2, x: 50, y: 0 });
    expect(types(events)).toEqual(["right.down", "right.up"]);
  });
  test("two-finger drag scrolls both axes and never clicks", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h);
    m.down({ id: 1, x: 0, y: 100 }); m.down({ id: 2, x: 50, y: 100 });
    m.move({ id: 1, x: 20, y: 60 }); m.move({ id: 2, x: 70, y: 60 }); fc.frame();
    m.up({ id: 1, x: 20, y: 60 }); m.up({ id: 2, x: 70, y: 60 });
    expect(types(events)).toEqual(["pointer.wheel"]);
    const w = events[0] as Extract<RemoteInputEvent, { type: "pointer.wheel" }>;
    expect(w.dy).toBeGreaterThan(0); expect(w.dx).toBeLessThan(0);
  });
  test("double click emits two full clicks", () => {
    const { h, events } = host(); new MouseEngine(h).doubleClick();
    expect(types(events)).toEqual(["left.down", "left.up", "left.down", "left.up"]);
  });
  test("reset releases a held button", () => {
    const { h, events } = host(); const m = new MouseEngine(h);
    m.toggleHold(); m.reset();
    expect(types(events)).toEqual(["left.down", "left.up"]); expect(m.isHolding).toBe(false);
  });
  test("second finger arriving after long-press drag releases left button before wheel events", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h);
    m.down({ id: 1, x: 100, y: 100 });
    fc.advance(600);
    expect(types(events)).toEqual(["left.down"]);
    expect(m.isHolding).toBe(true);

    m.down({ id: 2, x: 150, y: 100 });
    expect(m.isHolding).toBe(false);
    m.move({ id: 1, x: 100, y: 60 });
    m.move({ id: 2, x: 150, y: 60 });
    fc.frame();
    m.up({ id: 1, x: 100, y: 60 });
    m.up({ id: 2, x: 150, y: 60 });

    expect(types(events)).toEqual(["left.down", "left.up", "pointer.wheel"]);
  });
  test("second finger does not release an explicit manual hold", () => {
    const { h, fc, events } = host(); const m = new MouseEngine(h);
    m.toggleHold();
    m.down({ id: 1, x: 100, y: 100 });
    m.down({ id: 2, x: 150, y: 100 });
    m.move({ id: 1, x: 100, y: 60 });
    m.move({ id: 2, x: 150, y: 60 });
    fc.frame();
    m.up({ id: 1, x: 100, y: 60 });
    m.up({ id: 2, x: 150, y: 60 });
    expect(m.isHolding).toBe(true);
    expect(types(events)).toEqual(["left.down", "pointer.wheel"]);
  });
});

describe("touch mode", () => {
  test("tap clicks exactly where touched, accounting for letterbox offset", () => {
    const { h, fc, events } = host({ x: 100, y: 50, w: 800, h: 400 }); const t = new TouchEngine(h);
    t.down({ id: 1, x: 500, y: 250 }); fc.advance(80); t.up({ id: 1, x: 500, y: 250 });
    expect(types(events)).toEqual(["pointer.move.abs", "left.down", "left.up"]);
    expect(events[1]).toMatchObject({ x: 0.5, y: 0.5 });
  });
  test("tap on the black bar does nothing", () => {
    const { h, events } = host({ x: 100, y: 50, w: 800, h: 400 }); const t = new TouchEngine(h);
    t.down({ id: 1, x: 50, y: 250 }); t.up({ id: 1, x: 50, y: 250 });
    expect(events.length).toBe(0);
  });
  test("drag: down at start point, coalesced moves, up at last point", () => {
    const { h, fc, events } = host(); const t = new TouchEngine(h);
    t.down({ id: 1, x: 100, y: 100 }); t.move({ id: 1, x: 150, y: 100 }); t.move({ id: 1, x: 200, y: 100 }); fc.frame();
    t.up({ id: 1, x: 200, y: 100 });
    expect(types(events)).toEqual(["pointer.move.abs", "left.down", "pointer.move.abs", "left.up"]);
    expect(events[1]).toMatchObject({ x: 0.1 }); expect(events[3]).toMatchObject({ x: 0.2 });
  });
  test("long press is a right click and the release adds nothing", () => {
    const { h, fc, events } = host(); const t = new TouchEngine(h);
    t.down({ id: 1, x: 100, y: 100 }); fc.advance(550); t.up({ id: 1, x: 100, y: 100 });
    expect(types(events)).toEqual(["pointer.move.abs", "right.down", "right.up"]);
  });
  test("pinch zooms the local view and sends nothing to the laptop", () => {
    const { h, fc, events, getView } = host(); const t = new TouchEngine(h);
    t.down({ id: 1, x: 400, y: 250 }); t.down({ id: 2, x: 600, y: 250 });
    t.move({ id: 1, x: 300, y: 250 }); t.move({ id: 2, x: 700, y: 250 }); fc.frame();
    t.up({ id: 1, x: 300, y: 250 }); t.up({ id: 2, x: 700, y: 250 });
    expect(getView().zoom).toBeCloseTo(2, 1); expect(events.length).toBe(0);
  });
  test("a second finger during a drag ends the drag before the new gesture", () => {
    const { h, fc, events } = host(); const t = new TouchEngine(h);
    t.down({ id: 1, x: 100, y: 100 }); t.move({ id: 1, x: 150, y: 100 });
    t.down({ id: 2, x: 300, y: 100 });
    t.move({ id: 1, x: 150, y: 60 }); t.move({ id: 2, x: 300, y: 60 }); fc.frame();
    t.up({ id: 1, x: 150, y: 60 }); t.up({ id: 2, x: 300, y: 60 });
    expect(types(events)).toEqual(["pointer.move.abs", "left.down", "pointer.move.abs", "left.up", "pointer.wheel"]);
  });
  test("pinch simultaneously zooms and moves midpoint by (+50, +30) without double translation or drift", () => {
    const base = { x: 0, y: 0, w: 1000, h: 500 };
    const { h, getView } = host(base, { w: 1000, h: 500 });
    const t = new TouchEngine(h);
    // Start midpoint (400, 200) -> normalized focal remote point (0.4, 0.4), dist = 200.
    t.down({ id: 1, x: 300, y: 200 });
    t.down({ id: 2, x: 500, y: 200 });
    // Intermediate step (zoom = 1.5x, mid = (425, 215)) then final step (zoom = 2x, mid = (450, 230), delta = (+50, +30)).
    t.move({ id: 1, x: 275, y: 215 });
    t.move({ id: 2, x: 575, y: 215 });
    t.move({ id: 1, x: 250, y: 230 });
    t.move({ id: 2, x: 650, y: 230 });
    t.up({ id: 1, x: 250, y: 230 });
    t.up({ id: 2, x: 650, y: 230 });

    const v = getView();
    expect(v.zoom).toBeCloseTo(2, 6);
    expect(v.panX).toBeCloseTo(-350, 6);
    expect(v.panY).toBeCloseTo(-170, 6);
    const screenPt = fromRemote(0.4, 0.4, applyView(base, v));
    expect(screenPt.x).toBeCloseTo(450, 6);
    expect(screenPt.y).toBeCloseTo(230, 6);
  });
});

describe("keyboard engine", () => {
  test("normal key sends down on press and up on release", () => {
    const ev: RemoteInputEvent[] = []; const k = new KeyboardEngine((e) => ev.push(e));
    k.press("a"); k.release("a");
    expect(types(ev)).toEqual(["KeyA.down", "KeyA.up"]);
  });
  test("one-shot Ctrl wraps the next key and then releases", () => {
    const ev: RemoteInputEvent[] = []; const k = new KeyboardEngine((e) => ev.push(e));
    k.press("ctrl"); k.press("c"); k.release("c");
    expect(types(ev)).toEqual(["ControlLeft.down", "KeyC.down", "KeyC.up", "ControlLeft.up"]);
  });
  test("locked modifier stays down across keys", () => {
    const ev: RemoteInputEvent[] = []; const k = new KeyboardEngine((e) => ev.push(e));
    k.press("shift"); k.press("shift"); k.press("a"); k.release("a"); k.press("b"); k.release("b");
    expect(k.heldCodes).toEqual(["ShiftLeft"]);
  });
  test("releaseAll lifts every held key and modifier", () => {
    const ev: RemoteInputEvent[] = []; const k = new KeyboardEngine((e) => ev.push(e));
    k.press("ctrl"); k.press("alt"); k.press("alt"); k.press("delete");
    ev.length = 0; k.releaseAll();
    expect(types(ev).sort()).toEqual(["AltLeft.up", "ControlLeft.up", "Delete.up"]);
    expect(k.heldCodes).toEqual([]);
  });
});

describe("keyboard layouts", () => {
  test("default full layout has every required key", () => {
    const keys = new Set(defaultFull().rows.flat());
    for (const id of ["a", "z", "d0", "d9", "f1", "f12", "esc", "tab", "caps", "shift", "ctrl", "alt", "win", "space", "enter", "backspace", "delete", "insert", "home", "end", "pgup", "pgdn", "up", "down", "left", "right", "prtsc", "comma", "period"]) expect(keys.has(id)).toBe(true);
  });
  test("removed key becomes restorable and can be re-added", () => {
    let l = removeKey(defaultFull(), "prtsc");
    expect(availableKeys(l)).toContain("prtsc");
    l = addKey(l, 0, "prtsc", 0);
    expect(l.rows[0]![0]).toBe("prtsc"); expect(availableKeys(l)).not.toContain("prtsc");
  });
  test("adding a key already on the layout is ignored", () => {
    const l = defaultFull(); expect(addKey(l, 0, "a")).toBe(l);
  });
  test("reorder moves a key across rows", () => {
    const l = moveKey(defaultFull(), "esc", 1, 0);
    expect(l.rows[1]![0]).toBe("esc"); expect(l.rows[0]).not.toContain("esc");
  });
  test("restore default brings back the original keys", () => {
    expect(restoreDefault(removeKey(defaultFull(), "a")).rows).toEqual(defaultFull().rows);
  });
  test("custom layouts can be created and deleted; built-ins cannot", () => {
    let p = createLayout(defaultPrefs(), "Gaming", defaultFull());
    expect(p.layouts.length).toBe(3); expect(p.activeId).toBe(p.layouts[2]!.id);
    p = deleteLayout(p, p.activeId); expect(p.layouts.length).toBe(2); expect(p.activeId).toBe("full");
    expect(deleteLayout(p, "full").layouts.length).toBe(2);
  });
  test("prefs persist per user and survive reload", () => {
    const store = new Map<string, string>(); const s = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); } };
    const p = createLayout(defaultPrefs(), "Mine", removeKey(defaultFull(), "f1"));
    savePrefs(s, "user-1", p);
    const back = loadPrefs(s, "user-1");
    expect(back.activeId).toBe(p.activeId); expect(back.layouts[2]!.rows.flat()).not.toContain("f1");
    expect(loadPrefs(s, "user-2").layouts.length).toBe(2);
  });
  test("corrupt or tampered stored data falls back safely", () => {
    expect(parsePrefs("{nope").activeId).toBe("full");
    const p = parsePrefs(JSON.stringify({ version: 1, activeId: "x", layouts: [{ id: "c", name: "c", base: "full", rows: [["a", "<script>"]] }] }));
    expect(p.layouts[0]!.rows).toEqual([["a"]]); expect(p.activeId).toBe("full");
  });
});

describe("transport and viewport", () => {
  test("without a connection, input is never reported as delivered", () => {
    const bus = new InputBus(new NotConnectedTransport()); const seen: boolean[] = [];
    bus.subscribe((_, d) => seen.push(d)); bus.emit({ type: "key", code: "KeyA", action: "down" });
    expect(seen).toEqual([false]);
  });
  test("portrait detection", () => { expect(isPortrait(360, 800)).toBe(true); expect(isPortrait(800, 360)).toBe(false); });
  test("fullscreen support falls back when the browser lacks it", () => {
    expect(fullscreenSupported({})).toBe(false); expect(fullscreenSupported({ webkitFullscreenEnabled: true })).toBe(true);
  });
  test("disposing an engine cancels pending frames and timers", () => {
    const { h, fc } = host(); const m = new MouseEngine(h);
    m.down({ id: 1, x: 0, y: 0 }); m.move({ id: 1, x: 50, y: 0 });
    m.dispose(); expect(fc.frames.size).toBe(0); expect(fc.timers.size).toBe(0);
  });
});
