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

type SharedPairingFixture = {
  endpointPath: string;
  qrSchema: { type: string; version: number };
  validQrPayloads: Array<{
    name: string;
    json: string;
    expectedServer: string;
    expectedPairingId: string;
  }>;
  invalidQrPayloads: Array<{ name: string; json: string }>;
  validPinRequests: Array<{
    name: string;
    json: string;
  }>;
  validQrRequests: Array<{
    name: string;
    json: string;
  }>;
  invalidPairRequests: Array<{ name: string; json: string }>;
  serverResponses: {
    confirmed200: string;
    invalidCode401: string;
    expired410: string;
    tooManyAttempts429: string;
    serverError500: string;
  };
};

const PAIRING_FIXTURE_PATH = path.resolve(
  process.cwd(),
  "companion/windows/tests/Beacon.Companion.Tests/Fixtures/pairing-protocol-contract.json",
);

const pairingFixture: SharedPairingFixture = JSON.parse(readFileSync(PAIRING_FIXTURE_PATH, "utf8"));

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const BASE64URL_TOKEN_RE = /^[A-Za-z0-9_-]{20,128}$/;

function isValidBackendPairingBody(rawJson: string): boolean {
  let parsed: unknown;
  try {
    parsed = JSON.parse(rawJson);
  } catch {
    return false;
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return false;
  const obj = parsed as Record<string, unknown>;
  const device = obj["device"];
  if (!device || typeof device !== "object" || Array.isArray(device)) return false;
  const devObj = device as Record<string, unknown>;
  const validDevice =
    typeof devObj["name"] === "string" &&
    devObj["name"].trim().length >= 1 &&
    devObj["name"].trim().length <= 64 &&
    typeof devObj["os"] === "string" &&
    devObj["os"].trim().length >= 1 &&
    devObj["os"].trim().length <= 64 &&
    typeof devObj["agent_version"] === "string" &&
    devObj["agent_version"].trim().length >= 1 &&
    devObj["agent_version"].trim().length <= 32;
  if (!validDevice) return false;

  const hasCode = "code" in obj;
  const hasPairingId = "pairing_id" in obj;
  const hasToken = "token" in obj;

  if (hasCode && !hasPairingId && !hasToken) {
    return typeof obj["code"] === "string" && /^\d{6}$/.test(obj["code"]);
  }

  if (!hasCode && hasPairingId && hasToken) {
    return (
      typeof obj["pairing_id"] === "string" &&
      UUID_RE.test(obj["pairing_id"]) &&
      typeof obj["token"] === "string" &&
      obj["token"].length >= 20 &&
      obj["token"].length <= 128
    );
  }

  return false;
}

function isValidFrontendQrPayload(rawJson: string): boolean {
  let parsed: unknown;
  try {
    parsed = JSON.parse(rawJson);
  } catch {
    return false;
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return false;
  const obj = parsed as Record<string, unknown>;
  if (obj["type"] !== "beacon-pair" || obj["v"] !== 1) return false;
  if (
    typeof obj["server"] !== "string" ||
    typeof obj["pairing_id"] !== "string" ||
    typeof obj["token"] !== "string"
  ) {
    return false;
  }
  if (!UUID_RE.test(obj["pairing_id"]) || !BASE64URL_TOKEN_RE.test(obj["token"])) return false;

  let url: URL;
  try {
    url = new URL(obj["server"]);
  } catch {
    return false;
  }
  if (url.username || url.password || url.search || url.hash) return false;
  if (url.pathname !== "/" && url.pathname !== "") return false;
  if (url.protocol === "https:") return true;
  if (
    url.protocol === "http:" &&
    (url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]")
  ) {
    return true;
  }
  return false;
}

describe("cross-language TypeScript <-> C# Step 3.3 pairing protocol contract", () => {
  test("shared pairing fixture matches frontend QR generator and backend /api/public/agent/pair schema", () => {
    expect(pairingFixture.endpointPath).toBe("/api/public/agent/pair");
    expect(pairingFixture.qrSchema).toEqual({ type: "beacon-pair", version: 1 });

    for (const qr of pairingFixture.validQrPayloads) {
      expect(isValidFrontendQrPayload(qr.json), qr.name).toBe(true);
    }

    for (const qr of pairingFixture.invalidQrPayloads) {
      expect(isValidFrontendQrPayload(qr.json), qr.name).toBe(false);
    }

    for (const req of pairingFixture.validPinRequests) {
      expect(isValidBackendPairingBody(req.json), req.name).toBe(true);
    }

    for (const req of pairingFixture.validQrRequests) {
      expect(isValidBackendPairingBody(req.json), req.name).toBe(true);
    }

    for (const req of pairingFixture.invalidPairRequests) {
      expect(isValidBackendPairingBody(req.json), req.name).toBe(false);
    }

    const ok200 = JSON.parse(pairingFixture.serverResponses.confirmed200) as Record<
      string,
      unknown
    >;
    expect(UUID_RE.test(String(ok200["device_id"]))).toBe(true);
    expect(BASE64URL_TOKEN_RE.test(String(ok200["device_token"]))).toBe(true);

    for (const errJson of [
      pairingFixture.serverResponses.invalidCode401,
      pairingFixture.serverResponses.expired410,
      pairingFixture.serverResponses.tooManyAttempts429,
      pairingFixture.serverResponses.serverError500,
    ]) {
      const parsed = JSON.parse(errJson) as Record<string, unknown>;
      expect(typeof parsed["error"]).toBe("string");
    }
  });
});

type SharedHeartbeatFixture = {
  endpointPath: string;
  method: string;
  authorizationHeaderPrefix: string;
  tokenLength: { min: number; max: number };
  onlineWindowMs: number;
  recommendedIntervalMs: number;
  valid200Responses: Array<{ name: string; json: string }>;
  invalid200Responses: Array<{ name: string; json: string }>;
  revocation401Response: string;
  serverError500Response: string;
};

const HEARTBEAT_FIXTURE_PATH = path.resolve(
  process.cwd(),
  "companion/windows/tests/Beacon.Companion.Tests/Fixtures/heartbeat-protocol-contract.json",
);

const heartbeatFixture: SharedHeartbeatFixture = JSON.parse(
  readFileSync(HEARTBEAT_FIXTURE_PATH, "utf8"),
);

function isValidHeartbeat200FromTsContract(rawJson: string): boolean {
  let parsed: unknown;
  try {
    parsed = JSON.parse(rawJson);
  } catch {
    return false;
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return false;
  const obj = parsed as Record<string, unknown>;
  return (
    obj["ok"] === true &&
    typeof obj["server_time"] === "string" &&
    Number.isFinite(Date.parse(obj["server_time"]))
  );
}

describe("cross-language TypeScript <-> C# Step 3.4 heartbeat protocol contract", () => {
  test("shared heartbeat fixture matches /api/public/agent/heartbeat endpoint and response schema", async () => {
    const { HEARTBEAT_ONLINE_WINDOW_MS } = await import("@/lib/pairing");

    expect(heartbeatFixture.endpointPath).toBe("/api/public/agent/heartbeat");
    expect(heartbeatFixture.method).toBe("POST");
    expect(heartbeatFixture.authorizationHeaderPrefix).toBe("Bearer ");
    expect(heartbeatFixture.tokenLength).toEqual({ min: 20, max: 128 });
    expect(heartbeatFixture.onlineWindowMs).toBe(HEARTBEAT_ONLINE_WINDOW_MS);
    expect(heartbeatFixture.recommendedIntervalMs).toBeLessThan(heartbeatFixture.onlineWindowMs);

    for (const entry of heartbeatFixture.valid200Responses) {
      expect(isValidHeartbeat200FromTsContract(entry.json), entry.name).toBe(true);
    }

    for (const entry of heartbeatFixture.invalid200Responses) {
      expect(isValidHeartbeat200FromTsContract(entry.json), entry.name).toBe(false);
    }

    expect(JSON.parse(heartbeatFixture.revocation401Response)).toEqual({ error: "unauthorized" });
    expect(JSON.parse(heartbeatFixture.serverError500Response)).toEqual({ error: "server_error" });
  });
});
