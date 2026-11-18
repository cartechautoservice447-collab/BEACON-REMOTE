import { KEY_BY_ID, KEY_CATALOG } from "./keys";

export type KeyboardLayout = { id: string; name: string; base: "full" | "compact"; rows: string[][] };
export type KeyboardPrefs = { version: 1; activeId: string; layouts: KeyboardLayout[] };

const row = (s: string) => s.split(" ");

export function defaultFull(): KeyboardLayout {
  return {
    id: "full", name: "Full keyboard", base: "full",
    rows: [
      row("esc f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 f11 f12 prtsc"),
      row("backquote d1 d2 d3 d4 d5 d6 d7 d8 d9 d0 minus equal backspace"),
      row("tab q w e r t y u i o p lbracket rbracket backslash"),
      row("caps a s d f g h j k l semicolon quote enter"),
      row("shift z x c v b n m comma period slash up"),
      row("ctrl win alt space insert delete left down right"),
      row("home end pgup pgdn"),
    ],
  };
}

export function defaultCompact(): KeyboardLayout {
  return {
    id: "compact", name: "Compact keyboard", base: "compact",
    rows: [
      row("esc tab ctrl alt win delete backspace"),
      row("q w e r t y u i o p"),
      row("a s d f g h j k l enter"),
      row("shift z x c v b n m up"),
      row("comma space period left down right"),
    ],
  };
}

export const defaultPrefs = (): KeyboardPrefs => ({ version: 1, activeId: "full", layouts: [defaultFull(), defaultCompact()] });

const clone = (l: KeyboardLayout): KeyboardLayout => ({ ...l, rows: l.rows.map((r) => [...r]) });

/** Pure, immutable layout operations. */
export function addKey(l: KeyboardLayout, rowIdx: number, keyId: string, index?: number): KeyboardLayout {
  if (!KEY_BY_ID[keyId] || l.rows.some((r) => r.includes(keyId))) return l;
  const n = clone(l);
  if (rowIdx >= n.rows.length) n.rows.push([]);
  const r = n.rows[Math.max(0, rowIdx)]!;
  r.splice(index ?? r.length, 0, keyId);
  return n;
}

export function removeKey(l: KeyboardLayout, keyId: string): KeyboardLayout {
  const n = clone(l);
  n.rows = n.rows.map((r) => r.filter((id) => id !== keyId)).filter((r) => r.length > 0);
  return n;
}

/** Move a key within or across rows. */
export function moveKey(l: KeyboardLayout, keyId: string, toRow: number, toIndex: number): KeyboardLayout {
  if (!l.rows.some((r) => r.includes(keyId))) return l;
  const n = clone(l);
  const fromRow = n.rows.findIndex((r) => r.includes(keyId));
  n.rows[fromRow]!.splice(n.rows[fromRow]!.indexOf(keyId), 1);
  const target = Math.min(Math.max(0, toRow), n.rows.length);
  if (target === n.rows.length) n.rows.push([]);
  const r = n.rows[target]!;
  r.splice(Math.min(Math.max(0, toIndex), r.length), 0, keyId);
  n.rows = n.rows.filter((x) => x.length > 0);
  return n;
}

/** Keys from the catalog not currently on the layout (i.e. removed / restorable). */
export function availableKeys(l: KeyboardLayout): string[] {
  const used = new Set(l.rows.flat());
  return KEY_CATALOG.map((d) => d.id).filter((id) => !used.has(id));
}

export function restoreDefault(l: KeyboardLayout): KeyboardLayout {
  const base = l.base === "full" ? defaultFull() : defaultCompact();
  return { ...base, id: l.id, name: l.name };
}

export function createLayout(prefs: KeyboardPrefs, name: string, from: KeyboardLayout): KeyboardPrefs {
  const id = `custom-${Date.now().toString(36)}-${prefs.layouts.length}`;
  const layout = { ...clone(from), id, name: name.trim().slice(0, 40) || "My layout" };
  return { ...prefs, activeId: id, layouts: [...prefs.layouts, layout] };
}

export function updateLayout(prefs: KeyboardPrefs, layout: KeyboardLayout): KeyboardPrefs {
  return { ...prefs, layouts: prefs.layouts.map((l) => (l.id === layout.id ? layout : l)) };
}

export function deleteLayout(prefs: KeyboardPrefs, id: string): KeyboardPrefs {
  if (id === "full" || id === "compact") return prefs;
  const layouts = prefs.layouts.filter((l) => l.id !== id);
  return { ...prefs, layouts, activeId: prefs.activeId === id ? "full" : prefs.activeId };
}

export const activeLayout = (p: KeyboardPrefs): KeyboardLayout => p.layouts.find((l) => l.id === p.activeId) ?? p.layouts[0] ?? defaultFull();

/** Validates untrusted stored JSON; unknown keys are dropped, broken data falls back to defaults. */
export function parsePrefs(raw: string | null): KeyboardPrefs {
  if (!raw) return defaultPrefs();
  try {
    const v = JSON.parse(raw) as KeyboardPrefs;
    if (v?.version !== 1 || !Array.isArray(v.layouts)) return defaultPrefs();
    const layouts = v.layouts
      .filter((l) => l && typeof l.id === "string" && typeof l.name === "string" && Array.isArray(l.rows))
      .map((l) => ({ id: l.id, name: l.name.slice(0, 40), base: l.base === "compact" ? "compact" as const : "full" as const,
        rows: l.rows.map((r) => (Array.isArray(r) ? r.filter((id) => typeof id === "string" && KEY_BY_ID[id]) : [])).filter((r) => r.length) }));
    for (const d of [defaultFull(), defaultCompact()]) if (!layouts.some((l) => l.id === d.id)) layouts.push(d);
    return { version: 1, layouts, activeId: layouts.some((l) => l.id === v.activeId) ? v.activeId : "full" };
  } catch { return defaultPrefs(); }
}

export interface PrefsStorage { getItem(k: string): string | null; setItem(k: string, v: string): void }
export const prefsKey = (userId: string) => `beacon.keyboard.v1.${userId}`;
export const loadPrefs = (s: PrefsStorage, userId: string) => parsePrefs(s.getItem(prefsKey(userId)));
export const savePrefs = (s: PrefsStorage, userId: string, p: KeyboardPrefs) => s.setItem(prefsKey(userId), JSON.stringify(p));
