import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, test } from "vitest";
import { KEY_CATALOG } from "./keys";
import { KeyboardEngine } from "./keyboard-engine";
import { MouseEngine } from "./mouse-engine";
import { TouchEngine } from "./touch-engine";
import { IDENTITY_VIEW } from "./coords";
import type { Clock, RemoteInputEvent } from "./protocol";

type SharedProtocolFixture = {
  eventTypes: string[];
  mouseButtons: string[];
  inputActions: string[];
  coordinateRange: { min: number; max: number };
  modifierKeyCodes: string[];
  lockKeyCodes: string[];
  allKeyCodes: string[];
  validPayloads: Array<{ name: string; json: string; expectedType: string }>;
  invalidWirePayloads: Array<{ name: string; json: string; stage: string }>;
};

const FIXTURE_PATH = path.resolve(
  process.cwd(),
  "companion/windows/tests/Beacon.Companion.Tests/Fixtures/remote-protocol-contract.json",
);

const fixture: SharedProtocolFixture = JSON.parse(readFileSync(FIXTURE_PATH, "utf8"));

function isValidWireEventFromTsContract(rawJson: string): boolean {
  let parsed: unknown;
  try {
    parsed = JSON.parse(rawJson);
  } catch {
    return false;
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return false;
  const obj = parsed as Record<string, unknown>;
  const eventType = obj["type"];
  if (typeof eventType !== "string" || !fixture.eventTypes.includes(eventType)) return false;

  const inRange01 = (v: unknown) =>
    typeof v === "number" &&
    Number.isFinite(v) &&
    v >= fixture.coordinateRange.min &&
    v <= fixture.coordinateRange.max;

  switch (eventType) {
    case "pointer.move.abs":
      return inRange01(obj["x"]) && inRange01(obj["y"]);
    case "pointer.button":
      return (
        typeof obj["button"] === "string" &&
        fixture.mouseButtons.includes(obj["button"]) &&
        typeof obj["action"] === "string" &&
        fixture.inputActions.includes(obj["action"]) &&
        inRange01(obj["x"]) &&
        inRange01(obj["y"])
      );
    case "pointer.wheel":
      return (
        typeof obj["dx"] === "number" &&
        Number.isFinite(obj["dx"]) &&
        Math.abs(obj["dx"]) <= 10_000 &&
        typeof obj["dy"] === "number" &&
        Number.isFinite(obj["dy"]) &&
        Math.abs(obj["dy"]) <= 10_000
      );
    case "key":
      return (
        typeof obj["code"] === "string" &&
        fixture.allKeyCodes.includes(obj["code"]) &&
        typeof obj["action"] === "string" &&
        fixture.inputActions.includes(obj["action"])
      );
    case "input.reset":
      return true;
    default:
      return false;
  }
}

describe("cross-language TypeScript <-> C# protocol contract", () => {
  test("every key in KEY_CATALOG matches the shared C# KeyCodeCatalog fixture 1:1", () => {
    const tsCodes = KEY_CATALOG.map((k) => k.code);
    expect(tsCodes.length).toBe(80);
    expect(new Set(tsCodes).size).toBe(80);
    expect(new Set(tsCodes)).toEqual(new Set(fixture.allKeyCodes));

    const tsModifiers = KEY_CATALOG.filter((k) => k.kind === "modifier").map((k) => k.code);
    expect(new Set(tsModifiers)).toEqual(new Set(fixture.modifierKeyCodes));

    const tsLocks = KEY_CATALOG.filter((k) => k.kind === "lock").map((k) => k.code);
    expect(new Set(tsLocks)).toEqual(new Set(fixture.lockKeyCodes));
  });

  test("valid and invalid shared fixture payloads match TypeScript wire contract", () => {
    for (const entry of fixture.validPayloads) {
      expect(isValidWireEventFromTsContract(entry.json), entry.name).toBe(true);
      const parsed = JSON.parse(entry.json) as RemoteInputEvent;
      expect(parsed.type).toBe(entry.expectedType);
    }

    for (const entry of fixture.invalidWirePayloads) {
      expect(isValidWireEventFromTsContract(entry.json), entry.name).toBe(false);
    }
  });

  test("MouseEngine, TouchEngine, and KeyboardEngine emit JSON conforming to the shared contract", () => {
    const emitted: RemoteInputEvent[] = [];
    const frames: Array<() => void> = [];
    const clock: Clock = {
      now: () => 10,
      setTimeout: () => 1,
      clearTimeout: () => {},
      requestFrame: (fn) => {
        frames.push(fn);
        return frames.length;
      },
      cancelFrame: () => {},
    };
    const host = {
      clock,
      emit: (e: RemoteInputEvent) => emitted.push(e),
      container: () => ({ w: 1000, h: 500 }),
      base: () => ({ x: 0, y: 0, w: 1000, h: 500 }),
      rendered: () => ({ x: 0, y: 0, w: 1000, h: 500 }),
      view: () => IDENTITY_VIEW,
      setView: () => {},
    };

    const mouse = new MouseEngine(host);
    mouse.down({ id: 1, x: 100, y: 100 });
    mouse.move({ id: 1, x: 140, y: 120 });
    frames.splice(0).forEach((fn) => fn());
    mouse.click("middle");

    const touch = new TouchEngine(host, undefined);
    touch.down({ id: 1, x: 200, y: 100 });
    touch.down({ id: 2, x: 260, y: 100 });
    touch.move({ id: 1, x: 200, y: 50 });
    touch.move({ id: 2, x: 260, y: 50 });
    frames.splice(0).forEach((fn) => fn());
    touch.up({ id: 1, x: 200, y: 50 });
    touch.up({ id: 2, x: 260, y: 50 });

    const kb = new KeyboardEngine((e) => emitted.push(e));
    kb.press("ctrl");
    kb.press("a");
    kb.release("a");

    emitted.push({ type: "input.reset" });

    expect(emitted.length).toBeGreaterThanOrEqual(7);
    for (const ev of emitted) {
      expect(isValidWireEventFromTsContract(JSON.stringify(ev))).toBe(true);
    }
  });
});
