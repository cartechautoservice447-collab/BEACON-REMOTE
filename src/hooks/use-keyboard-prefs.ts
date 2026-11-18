import { useCallback, useEffect, useRef, useState } from "react";
import { defaultPrefs, loadPrefs, savePrefs, type KeyboardPrefs } from "@/lib/remote/keyboard-layout";

function readUserPrefs(userId: string | undefined): KeyboardPrefs {
  if (!userId || typeof localStorage === "undefined") return defaultPrefs();
  try {
    return loadPrefs(localStorage, userId);
  } catch {
    return defaultPrefs();
  }
}

/** Keyboard layouts saved per signed-in user on this device. */
export function useKeyboardPrefs(userId: string | undefined) {
  const [prefs, setPrefs] = useState<KeyboardPrefs>(() => readUserPrefs(userId));
  const loadedForRef = useRef<string | undefined>(userId);

  useEffect(() => {
    if (loadedForRef.current === userId) return;
    loadedForRef.current = userId;
    setPrefs(readUserPrefs(userId));
  }, [userId]);

  const update = useCallback(
    (next: KeyboardPrefs) => {
      setPrefs(next);
      if (userId && typeof localStorage !== "undefined") {
        try {
          savePrefs(localStorage, userId, next);
        } catch {
          /* storage full or blocked */
        }
      }
    },
    [userId],
  );
  return [prefs, update] as const;
}
