/** Key catalog. `code` follows KeyboardEvent.code so the future agent can map it to Windows virtual keys. */
export type KeyKind = "key" | "modifier" | "lock";
export type KeyDef = { id: string; label: string; code: string; kind: KeyKind; width?: number; shiftLabel?: string };

const k = (id: string, label: string, code: string, extra: Partial<KeyDef> = {}): KeyDef => ({ id, label, code, kind: "key", ...extra });

const letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("").map((c) => k(c.toLowerCase(), c, `Key${c}`));
const digits = "1234567890".split("").map((d, i) => k(`d${d}`, d, `Digit${d}`, { shiftLabel: "!@#$%^&*()"[i]! }));
const fkeys = Array.from({ length: 12 }, (_, i) => k(`f${i + 1}`, `F${i + 1}`, `F${i + 1}`));

export const KEY_CATALOG: KeyDef[] = [
  ...letters, ...digits, ...fkeys,
  k("esc", "Esc", "Escape"), k("tab", "Tab", "Tab", { width: 1.5 }),
  { id: "caps", label: "Caps", code: "CapsLock", kind: "lock", width: 1.75 },
  { id: "shift", label: "Shift", code: "ShiftLeft", kind: "modifier", width: 2 },
  { id: "ctrl", label: "Ctrl", code: "ControlLeft", kind: "modifier", width: 1.25 },
  { id: "alt", label: "Alt", code: "AltLeft", kind: "modifier", width: 1.25 },
  { id: "win", label: "Win", code: "MetaLeft", kind: "modifier", width: 1.25 },
  k("space", "Space", "Space", { width: 5 }), k("enter", "Enter", "Enter", { width: 2 }),
  k("backspace", "⌫", "Backspace", { width: 1.5 }), k("delete", "Del", "Delete"), k("insert", "Ins", "Insert"),
  k("home", "Home", "Home"), k("end", "End", "End"), k("pgup", "PgUp", "PageUp"), k("pgdn", "PgDn", "PageDown"),
  k("up", "↑", "ArrowUp"), k("down", "↓", "ArrowDown"), k("left", "←", "ArrowLeft"), k("right", "→", "ArrowRight"),
  k("prtsc", "PrtSc", "PrintScreen"),
  k("minus", "-", "Minus", { shiftLabel: "_" }), k("equal", "=", "Equal", { shiftLabel: "+" }),
  k("lbracket", "[", "BracketLeft", { shiftLabel: "{" }), k("rbracket", "]", "BracketRight", { shiftLabel: "}" }),
  k("backslash", "\\", "Backslash", { shiftLabel: "|" }), k("semicolon", ";", "Semicolon", { shiftLabel: ":" }),
  k("quote", "'", "Quote", { shiftLabel: '"' }), k("comma", ",", "Comma", { shiftLabel: "<" }),
  k("period", ".", "Period", { shiftLabel: ">" }), k("slash", "/", "Slash", { shiftLabel: "?" }),
  k("backquote", "`", "Backquote", { shiftLabel: "~" }),
];

export const KEY_BY_ID: Record<string, KeyDef> = Object.fromEntries(KEY_CATALOG.map((d) => [d.id, d]));
