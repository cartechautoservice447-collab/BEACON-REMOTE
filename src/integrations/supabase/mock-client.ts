import type { AuthChangeEvent, Session, User } from "@supabase/supabase-js";

export function isMockAuthAllowed(): boolean {
  if (typeof import.meta !== "undefined" && import.meta.env && import.meta.env.PROD) {
    return false;
  }
  if (typeof process !== "undefined" && process.env["NODE_ENV"] === "production") {
    return false;
  }
  return true;
}

async function hashMockPassword(password: string): Promise<string> {
  if (typeof crypto !== "undefined" && crypto.subtle) {
    const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(`mock-pw:${password}`));
    return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, "0")).join("");
  }
  return `plain:${password}`;
}

const SESSION_KEY = "beacon_mock_supabase_session";
const USERS_KEY = "beacon_mock_supabase_users";
const PROFILES_KEY = "beacon_mock_supabase_profiles";
const DEVICES_KEY = "beacon_mock_supabase_devices";
const PAIRING_KEY = "beacon_mock_supabase_pairing";

type StoredUser = {
  id: string;
  email: string;
  passwordHash: string;
  createdAt: string;
};

type RowRecord = Record<string, unknown>;

const memoryTables: Record<string, RowRecord[]> = {
  profiles: [],
  devices: [],
  device_credentials: [],
  pairing_sessions: [],
  pairing_attempts: [],
};

const memoryUsers = new Map<string, StoredUser>();
let memorySession: Session | null = null;
const authListeners = new Set<(event: AuthChangeEvent, session: Session | null) => void>();

function hasBrowserStorage(): boolean {
  return typeof window !== "undefined" && typeof window.localStorage !== "undefined";
}

function readJson<T>(key: string, fallback: T): T {
  if (!hasBrowserStorage()) return fallback;
  try {
    const raw = window.localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

function writeJson(key: string, value: unknown): void {
  if (!hasBrowserStorage()) return;
  try {
    if (value === null) {
      window.localStorage.removeItem(key);
    } else {
      window.localStorage.setItem(key, JSON.stringify(value));
    }
  } catch {
    // Ignore storage quota or sandbox restrictions.
  }
}

function getTable(name: string): RowRecord[] {
  if (hasBrowserStorage()) {
    if (name === "profiles") return readJson<RowRecord[]>(PROFILES_KEY, []);
    if (name === "devices") return readJson<RowRecord[]>(DEVICES_KEY, []);
    if (name === "pairing_sessions") return readJson<RowRecord[]>(PAIRING_KEY, []);
  }
  if (!memoryTables[name]) memoryTables[name] = [];
  return memoryTables[name];
}

function setTable(name: string, rows: RowRecord[]): void {
  if (hasBrowserStorage()) {
    if (name === "profiles") writeJson(PROFILES_KEY, rows);
    else if (name === "devices") writeJson(DEVICES_KEY, rows);
    else if (name === "pairing_sessions") writeJson(PAIRING_KEY, rows);
  }
  memoryTables[name] = rows;
}

function loadUsers(): StoredUser[] {
  if (hasBrowserStorage()) {
    return readJson<StoredUser[]>(USERS_KEY, []);
  }
  return [...memoryUsers.values()];
}

function saveUsers(users: StoredUser[]): void {
  memoryUsers.clear();
  for (const u of users) memoryUsers.set(u.email.toLowerCase(), u);
  if (hasBrowserStorage()) {
    writeJson(USERS_KEY, users);
  }
}

function toBase64Url(str: string): string {
  const bytes = new TextEncoder().encode(str);
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fromBase64Url(str: string): string {
  const padded = str.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (str.length % 4)) % 4);
  const bin = atob(padded);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return new TextDecoder().decode(bytes);
}

export function createMockJwt(userId: string, email: string): string {
  const header = toBase64Url(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const payload = toBase64Url(
    JSON.stringify({
      sub: userId,
      email,
      role: "authenticated",
      exp: Math.floor(Date.now() / 1000) + 60 * 60 * 24,
    }),
  );
  return `${header}.${payload}.mock-signature`;
}

export function decodeMockJwtClaims(token: string): { sub: string; email?: string } | null {
  if (!isMockAuthAllowed()) {
    throw new Error("Mock Supabase client is disabled in production.");
  }
  const parts = token.split(".");
  if (parts.length !== 3 || !parts[1]) return null;
  try {
    const parsed = JSON.parse(fromBase64Url(parts[1])) as { sub?: unknown; email?: unknown };
    if (typeof parsed.sub !== "string" || !parsed.sub) return null;
    return {
      sub: parsed.sub,
      ...(typeof parsed.email === "string" ? { email: parsed.email } : {}),
    };
  } catch {
    return null;
  }
}

function makeUser(id: string, email: string, createdAt: string): User {
  return {
    id,
    app_metadata: { provider: "email" },
    user_metadata: { display_name: email.split("@")[0] ?? "User" },
    aud: "authenticated",
    created_at: createdAt,
    email,
    role: "authenticated",
    updated_at: createdAt,
  } as User;
}

function makeSession(user: User): Session {
  const token = createMockJwt(user.id, user.email ?? "");
  return {
    access_token: token,
    refresh_token: `refresh-${user.id}`,
    expires_in: 86400,
    expires_at: Math.floor(Date.now() / 1000) + 86400,
    token_type: "bearer",
    user,
  };
}

function getStoredSession(): Session | null {
  if (hasBrowserStorage()) {
    return readJson<Session | null>(SESSION_KEY, null);
  }
  return memorySession;
}

function setStoredSession(session: Session | null): void {
  memorySession = session;
  if (hasBrowserStorage()) {
    writeJson(SESSION_KEY, session);
  }
}

function notifyAuth(event: AuthChangeEvent, session: Session | null): void {
  for (const listener of authListeners) {
    try {
      listener(event, session);
    } catch {
      // Ignore subscriber errors.
    }
  }
}

function ensureProfile(user: User): void {
  const profiles = getTable("profiles");
  if (!profiles.some((p) => p["id"] === user.id)) {
    const now = new Date().toISOString();
    setTable("profiles", [
      ...profiles,
      {
        id: user.id,
        display_name: (user.email ?? "").split("@")[0] || "User",
        created_at: now,
        updated_at: now,
      },
    ]);
  }
}

function generateUuid(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

type QueryFilter =
  | { op: "eq"; col: string; val: unknown }
  | { op: "lt"; col: string; val: unknown }
  | { op: "gte"; col: string; val: unknown };

type QueryResult<T = unknown> = {
  data: T;
  error: { message: string; code?: string } | null;
  count?: number;
};

type QueryScope = { kind: "admin" } | { kind: "user"; getUserId: () => string | null };

const USER_WRITABLE_DEVICE_FIELDS = new Set(["name"]);
const PAIRING_SELECT_HIDDEN = new Set(["code_hash", "token_hash"]);

class MockQueryBuilder implements PromiseLike<QueryResult> {
  private table: string;
  private scope: QueryScope;
  private mode: "select" | "insert" | "update" | "delete" = "select";
  private filters: QueryFilter[] = [];
  private payload: RowRecord | RowRecord[] | null = null;
  private orderBy: { col: string; ascending: boolean } | null = null;
  private headOnly = false;
  private wantCount = false;
  private wantSingle: "single" | "maybeSingle" | null = null;

  constructor(table: string, scope: QueryScope) {
    this.table = table;
    this.scope = scope;
  }

  select(_columns?: string, opts?: { count?: "exact"; head?: boolean }): this {
    if (this.mode !== "insert" && this.mode !== "update" && this.mode !== "delete") {
      this.mode = "select";
    }
    if (opts?.count === "exact") this.wantCount = true;
    if (opts?.head) this.headOnly = true;
    return this;
  }

  insert(values: RowRecord | RowRecord[]): this {
    this.mode = "insert";
    this.payload = values;
    return this;
  }

  update(values: RowRecord): this {
    this.mode = "update";
    this.payload = values;
    return this;
  }

  delete(opts?: { count?: "exact" }): this {
    this.mode = "delete";
    if (opts?.count === "exact") this.wantCount = true;
    return this;
  }

  eq(col: string, val: unknown): this {
    this.filters.push({ op: "eq", col, val });
    return this;
  }

  lt(col: string, val: unknown): this {
    this.filters.push({ op: "lt", col, val });
    return this;
  }

  gte(col: string, val: unknown): this {
    this.filters.push({ op: "gte", col, val });
    return this;
  }

  order(col: string, opts?: { ascending?: boolean }): this {
    this.orderBy = { col, ascending: opts?.ascending ?? true };
    return this;
  }

  single(): this {
    this.wantSingle = "single";
    return this;
  }

  maybeSingle(): this {
    this.wantSingle = "maybeSingle";
    return this;
  }

  private matches(row: RowRecord): boolean {
    return this.filters.every((f) => {
      const v = row[f.col];
      if (f.op === "eq") return v === f.val;
      if (f.op === "lt") return typeof v === "string" && typeof f.val === "string" && v < f.val;
      if (f.op === "gte") return typeof v === "string" && typeof f.val === "string" && v >= f.val;
      return true;
    });
  }

  private checkUserPermission(uid: string | null): QueryResult | null {
    if (this.scope.kind === "admin") return null;

    if (this.table === "device_credentials" || this.table === "pairing_attempts") {
      return { data: null, error: { message: `permission denied for table ${this.table}` } };
    }

    if (this.mode !== "select" && !uid) {
      return { data: null, error: { message: `new row violates row-level security policy for table "${this.table}"` } };
    }

    if (this.table === "devices" && this.mode === "insert") {
      return { data: null, error: { message: "permission denied for table devices" } };
    }
    if (this.table === "profiles" && (this.mode === "insert" || this.mode === "delete")) {
      return { data: null, error: { message: "permission denied for table profiles" } };
    }
    if (this.table === "pairing_sessions" && this.mode !== "select") {
      return { data: null, error: { message: "permission denied for table pairing_sessions" } };
    }
    return null;
  }

  private ownsRow(row: RowRecord, uid: string | null): boolean {
    if (this.scope.kind === "admin") return true;
    if (!uid) return false;
    if (this.table === "profiles") return row["id"] === uid;
    if (this.table === "devices" || this.table === "pairing_sessions") return row["user_id"] === uid;
    return false;
  }

  private projectRow(row: RowRecord): RowRecord {
    if (this.scope.kind === "user" && this.table === "pairing_sessions") {
      const out: RowRecord = {};
      for (const [k, v] of Object.entries(row)) {
        if (!PAIRING_SELECT_HIDDEN.has(k)) out[k] = v;
      }
      return out;
    }
    return row;
  }

  private sanitizeUserPatch(patch: RowRecord): RowRecord {
    if (this.scope.kind === "admin") return patch;
    if (this.table === "devices") {
      const out: RowRecord = {};
      for (const [k, v] of Object.entries(patch)) {
        if (USER_WRITABLE_DEVICE_FIELDS.has(k)) out[k] = v;
      }
      return out;
    }
    if (this.table === "profiles") {
      const out: RowRecord = {};
      for (const [k, v] of Object.entries(patch)) {
        if (k !== "id" && k !== "created_at") out[k] = v;
      }
      return out;
    }
    return {};
  }

  private execute(): QueryResult {
    const uid = this.scope.kind === "user" ? this.scope.getUserId() : null;
    const denied = this.checkUserPermission(uid);
    if (denied) return denied;

    const rows = getTable(this.table);
    const now = new Date().toISOString();

    if (this.mode === "insert") {
      const items = Array.isArray(this.payload) ? this.payload : [this.payload ?? {}];
      const inserted = items.map((item) => ({
        id: generateUuid(),
        created_at: now,
        updated_at: now,
        registered_at: now,
        status: this.table === "pairing_sessions" ? "pending" : this.table === "devices" ? "offline" : undefined,
        failed_attempts: 0,
        ...item,
      }));
      setTable(this.table, [...rows, ...inserted]);
      if (this.wantSingle) {
        return { data: inserted[0] ?? null, error: null };
      }
      return { data: inserted, error: null };
    }

    if (this.mode === "update") {
      const patch = this.sanitizeUserPatch((this.payload as RowRecord) ?? {});
      const updatedRows: RowRecord[] = [];
      const next = rows.map((row) => {
        if (!this.ownsRow(row, uid) || !this.matches(row)) return row;
        const merged = { ...row, ...patch, updated_at: now };
        updatedRows.push(merged);
        return merged;
      });
      setTable(this.table, next);
      if (this.wantSingle) {
        return { data: updatedRows[0] ?? null, error: null };
      }
      return { data: updatedRows, error: null };
    }

    if (this.mode === "delete") {
      const remaining: RowRecord[] = [];
      let deletedCount = 0;
      for (const row of rows) {
        if (this.ownsRow(row, uid) && this.matches(row)) deletedCount++;
        else remaining.push(row);
      }
      setTable(this.table, remaining);
      return {
        data: null,
        error: null,
        ...(this.wantCount ? { count: deletedCount } : {}),
      };
    }

    let matched = rows.filter((r) => this.ownsRow(r, uid) && this.matches(r)).map((r) => this.projectRow(r));
    if (this.orderBy) {
      const { col, ascending } = this.orderBy;
      matched = [...matched].sort((a, b) => {
        const av = String(a[col] ?? "");
        const bv = String(b[col] ?? "");
        return ascending ? av.localeCompare(bv) : bv.localeCompare(av);
      });
    }

    if (this.headOnly) {
      return { data: null, error: null, count: matched.length };
    }
    if (this.wantSingle === "single") {
      return matched[0]
        ? { data: matched[0], error: null }
        : { data: null, error: { message: "Row not found" } };
    }
    if (this.wantSingle === "maybeSingle") {
      return { data: matched[0] ?? null, error: null };
    }
    return {
      data: matched,
      error: null,
      ...(this.wantCount ? { count: matched.length } : {}),
    };
  }

  then<TResult1 = QueryResult, TResult2 = never>(
    onfulfilled?: ((value: QueryResult) => TResult1 | PromiseLike<TResult1>) | null,
    onrejected?: ((reason: unknown) => TResult2 | PromiseLike<TResult2>) | null,
  ): Promise<TResult1 | TResult2> {
    return Promise.resolve(this.execute()).then(onfulfilled, onrejected);
  }
}

class MockChannel {
  on() {
    return this;
  }
  subscribe() {
    return this;
  }
}

function buildMockClient(scope: QueryScope) {
  return {
    auth: {
      async getSession() {
        return { data: { session: getStoredSession() }, error: null };
      },
      async getUser() {
        const session = getStoredSession();
        return { data: { user: session?.user ?? null }, error: null };
      },
      onAuthStateChange(callback: (event: AuthChangeEvent, session: Session | null) => void) {
        authListeners.add(callback);
        return {
          data: {
            subscription: {
              unsubscribe: () => {
                authListeners.delete(callback);
              },
            },
          },
        };
      },
      async signInWithPassword({ email, password }: { email: string; password: string }) {
        const normalized = email.trim().toLowerCase();
        const users = loadUsers();
        const existing = users.find((u) => u.email.toLowerCase() === normalized);
        if (!existing) {
          return { data: { user: null, session: null }, error: new Error("Invalid login credentials") };
        }
        const passwordHash = await hashMockPassword(password);
        if (existing.passwordHash !== passwordHash) {
          return { data: { user: null, session: null }, error: new Error("Invalid login credentials") };
        }
        const user = makeUser(existing.id, existing.email, existing.createdAt);
        const session = makeSession(user);
        ensureProfile(user);
        setStoredSession(session);
        notifyAuth("SIGNED_IN", session);
        return { data: { user, session }, error: null };
      },
      async signUp({ email, password }: { email: string; password: string; options?: unknown }) {
        const normalized = email.trim().toLowerCase();
        const users = loadUsers();
        if (users.some((u) => u.email.toLowerCase() === normalized)) {
          return { data: { user: null, session: null }, error: new Error("User already registered") };
        }
        const createdAt = new Date().toISOString();
        const passwordHash = await hashMockPassword(password);
        const record: StoredUser = { id: generateUuid(), email: email.trim(), passwordHash, createdAt };
        saveUsers([...users, record]);
        const user = makeUser(record.id, record.email, createdAt);
        const session = makeSession(user);
        ensureProfile(user);
        setStoredSession(session);
        notifyAuth("SIGNED_IN", session);
        return { data: { user, session }, error: null };
      },
      async resetPasswordForEmail(_email: string, _options?: unknown) {
        return { data: {}, error: null };
      },
      async updateUser({ password }: { password?: string }) {
        const session = getStoredSession();
        if (!session?.user) {
          return { data: { user: null }, error: new Error("Not authenticated") };
        }
        if (password) {
          const passwordHash = await hashMockPassword(password);
          const users = loadUsers().map((u) =>
            u.id === session.user.id ? { ...u, passwordHash } : u,
          );
          saveUsers(users);
        }
        notifyAuth("USER_UPDATED", session);
        return { data: { user: session.user }, error: null };
      },
      async signOut() {
        setStoredSession(null);
        notifyAuth("SIGNED_OUT", null);
        return { error: null };
      },
      async setSession(tokens: { access_token: string; refresh_token: string }) {
        const claims = decodeMockJwtClaims(tokens.access_token);
        const user = makeUser(claims?.sub ?? generateUuid(), claims?.email ?? "user@example.com", new Date().toISOString());
        const session: Session = {
          access_token: tokens.access_token,
          refresh_token: tokens.refresh_token,
          expires_in: 86400,
          expires_at: Math.floor(Date.now() / 1000) + 86400,
          token_type: "bearer",
          user,
        };
        setStoredSession(session);
        notifyAuth("SIGNED_IN", session);
        return { data: { user, session }, error: null };
      },
      async getClaims(token: string) {
        const claims = decodeMockJwtClaims(token);
        if (!claims) return { data: null, error: new Error("Invalid token") };
        return { data: { claims }, error: null };
      },
    },
    from(table: string) {
      return new MockQueryBuilder(table, scope);
    },
    rpc(fn: string, args: Record<string, unknown>) {
      if (scope.kind !== "admin") {
        return Promise.resolve({ data: null, error: { message: `permission denied for function ${fn}` } });
      }
      if (fn === "confirm_pairing") {
        const sessions = getTable("pairing_sessions");
        const pairingId = args["_pairing_id"];
        const codeHash = args["_code_hash"];
        const tokenHash = args["_token_hash"];
        const nowIso = new Date().toISOString();

        const idx = sessions.findIndex((s) =>
          pairingId ? s["id"] === pairingId : s["code_hash"] === codeHash && s["status"] === "pending",
        );
        if (idx === -1) {
          return Promise.resolve({ data: [{ device_id: null, result: "invalid" }], error: null });
        }
        const session = sessions[idx]!;
        if (session["status"] !== "pending") {
          return Promise.resolve({ data: [{ device_id: null, result: "invalid" }], error: null });
        }
        if (String(session["expires_at"] ?? "") < nowIso) {
          sessions[idx] = { ...session, status: "expired" };
          setTable("pairing_sessions", [...sessions]);
          return Promise.resolve({ data: [{ device_id: null, result: "expired" }], error: null });
        }
        if (pairingId && session["token_hash"] !== tokenHash) {
          const attempts = Number(session["failed_attempts"] ?? 0) + 1;
          sessions[idx] = {
            ...session,
            failed_attempts: attempts,
            status: attempts >= 5 ? "failed" : session["status"],
          };
          setTable("pairing_sessions", [...sessions]);
          return Promise.resolve({ data: [{ device_id: null, result: "invalid" }], error: null });
        }

        const deviceId = generateUuid();
        const devices = getTable("devices");
        setTable("devices", [
          ...devices,
          {
            id: deviceId,
            user_id: session["user_id"],
            name: args["_name"],
            os: args["_os"],
            agent_version: args["_agent_version"],
            status: "offline",
            last_heartbeat_at: null,
            last_connected_at: null,
            registered_at: nowIso,
            updated_at: nowIso,
          },
        ]);
        const creds = getTable("device_credentials");
        setTable("device_credentials", [
          ...creds,
          {
            device_id: deviceId,
            token_hash: args["_device_token_hash"],
            created_at: nowIso,
          },
        ]);
        sessions[idx] = { ...session, status: "confirmed", confirmed_at: nowIso, device_id: deviceId };
        setTable("pairing_sessions", [...sessions]);
        return Promise.resolve({ data: [{ device_id: deviceId, result: "confirmed" }], error: null });
      }
      return Promise.resolve({ data: null, error: { message: `Unknown RPC: ${fn}` } });
    },
    channel(_name: string) {
      return new MockChannel();
    },
    removeChannel(_channel: unknown) {
      return Promise.resolve("ok");
    },
  };
}

export function createMockSupabaseClient() {
  if (!isMockAuthAllowed()) {
    throw new Error("Mock Supabase client is disabled in production.");
  }
  return buildMockClient({ kind: "user", getUserId: () => getStoredSession()?.user?.id ?? null });
}

export function createMockSupabaseUserClient(userId: string) {
  if (!isMockAuthAllowed()) {
    throw new Error("Mock Supabase client is disabled in production.");
  }
  if (typeof window !== "undefined") {
    throw new Error("Server-scoped mock user client cannot be instantiated in the browser.");
  }
  return buildMockClient({ kind: "user", getUserId: () => userId });
}

export function createMockSupabaseAdminClient() {
  if (!isMockAuthAllowed()) {
    throw new Error("Mock Supabase client is disabled in production.");
  }
  if (typeof window !== "undefined") {
    throw new Error("Mock admin Supabase client is restricted to server environments.");
  }
  return buildMockClient({ kind: "admin" });
}
