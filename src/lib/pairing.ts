// Pure helpers shared by pairing server code. Uses Web Crypto (works in Workers).
export const PAIRING_TTL_SECONDS = 600; // 10 minutes
export const MAX_TOKEN_ATTEMPTS = 5;
export const MAX_FAILED_PER_CLIENT = 10; // per 15 minutes
export const HEARTBEAT_ONLINE_WINDOW_MS = 90_000;

export async function sha256Hex(input: string): Promise<string> {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(input));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function randomToken(bytes = 32): string {
  const a = crypto.getRandomValues(new Uint8Array(bytes));
  return btoa(String.fromCharCode(...a)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function randomSixDigitCode(): string {
  // Rejection sampling to avoid modulo bias.
  const a = new Uint32Array(1);
  for (;;) {
    crypto.getRandomValues(a);
    const v = a[0]!;
    if (v < 4_294_000_000) return String(v % 1_000_000).padStart(6, "0");
  }
}

export const isValidCode = (c: string) => /^\d{6}$/.test(c);
export const hashCode = (code: string) => sha256Hex(`pair-code:${code}`);
export const hashToken = (token: string) => sha256Hex(`pair-token:${token}`);
export const hashDeviceToken = (token: string) => sha256Hex(`device-token:${token}`);

/** A device is online only when its agent sent a heartbeat recently. */
export function isDeviceOnline(lastHeartbeatAt: string | null, now = Date.now()): boolean {
  if (!lastHeartbeatAt) return false;
  const t = Date.parse(lastHeartbeatAt);
  return Number.isFinite(t) && now - t <= HEARTBEAT_ONLINE_WINDOW_MS;
}

export function secondsRemaining(expiresAt: string, now = Date.now()): number {
  return Math.max(0, Math.floor((Date.parse(expiresAt) - now) / 1000));
}
