/** Browser capability helpers; all take the objects explicitly so they can be tested. */
export const isPortrait = (w: number, h: number) => h > w;

type FsDoc = { fullscreenEnabled?: boolean; webkitFullscreenEnabled?: boolean; fullscreenElement?: Element | null; webkitFullscreenElement?: Element | null };

export function fullscreenSupported(doc: FsDoc) { return Boolean(doc.fullscreenEnabled || doc.webkitFullscreenEnabled); }
export function isFullscreen(doc: FsDoc) { return Boolean(doc.fullscreenElement || doc.webkitFullscreenElement); }

/** Must be called from a user gesture. Resolves to what actually happened. */
export async function enterFullscreenLandscape(el: HTMLElement, doc: Document = document): Promise<{ fullscreen: boolean; locked: boolean }> {
  let fullscreen = false, locked = false;
  if (fullscreenSupported(doc as FsDoc)) {
    try {
      const anyEl = el as HTMLElement & { webkitRequestFullscreen?: () => Promise<void> };
      await (el.requestFullscreen ? el.requestFullscreen({ navigationUI: "hide" }) : anyEl.webkitRequestFullscreen?.());
      fullscreen = isFullscreen(doc as FsDoc);
    } catch { fullscreen = false; }
  }
  const o = (typeof screen !== "undefined" ? screen.orientation : undefined) as (ScreenOrientation & { lock?: (o: string) => Promise<void> }) | undefined;
  if (fullscreen && o?.lock) { try { await o.lock("landscape"); locked = true; } catch { locked = false; } }
  return { fullscreen, locked };
}

export async function exitFullscreen(doc: Document = document) {
  try { screen.orientation?.unlock?.(); } catch { /* not supported */ }
  if (isFullscreen(doc as FsDoc)) { try { await doc.exitFullscreen(); } catch { /* ignore */ } }
}
