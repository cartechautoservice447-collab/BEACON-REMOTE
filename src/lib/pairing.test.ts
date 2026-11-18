import { describe, expect, it } from "vitest";
import { isDeviceOnline, isValidCode, randomSixDigitCode, hashCode, secondsRemaining } from "./pairing";

describe("pairing rules", () => {
  it("device without heartbeat is offline", () => expect(isDeviceOnline(null)).toBe(false));
  it("heartbeat 60s ago is online", () =>
    expect(isDeviceOnline(new Date(1_000_000 - 60_000).toISOString(), 1_000_000)).toBe(true));
  it("heartbeat 2 minutes ago is offline", () =>
    expect(isDeviceOnline(new Date(1_000_000 - 120_000).toISOString(), 1_000_000)).toBe(false));
  it("codes are exactly six digits", () => {
    for (let i = 0; i < 200; i++) expect(isValidCode(randomSixDigitCode())).toBe(true);
    expect(isValidCode("12345")).toBe(false);
    expect(isValidCode("12a456")).toBe(false);
  });
  it("code hash never equals the code", async () => {
    const h = await hashCode("123456");
    expect(h).not.toContain("123456");
    expect(h).toHaveLength(64);
  });
  it("expired session has zero seconds remaining", () =>
    expect(secondsRemaining(new Date(0).toISOString(), 5000)).toBe(0));
});
