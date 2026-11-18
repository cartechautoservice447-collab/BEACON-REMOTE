import type { FitMode } from "./protocol";

export type Size = { w: number; h: number };
export type Rect = { x: number; y: number; w: number; h: number };
/** Local zoom/pan applied to the rendered screen (pinch-to-zoom). */
export type ViewTransform = { zoom: number; panX: number; panY: number };
export const IDENTITY_VIEW: ViewTransform = { zoom: 1, panX: 0, panY: 0 };
export const MAX_ZOOM = 4;

/** Where the remote frame is drawn inside the container, preserving aspect ratio (letterboxed for fit, cropped for fill). */
export function contentRect(container: Size, source: Size, mode: FitMode): Rect {
  if (container.w <= 0 || container.h <= 0 || source.w <= 0 || source.h <= 0) return { x: 0, y: 0, w: 0, h: 0 };
  const sx = container.w / source.w, sy = container.h / source.h;
  const s = mode === "fit" ? Math.min(sx, sy) : Math.max(sx, sy);
  const w = source.w * s, h = source.h * s;
  return { x: (container.w - w) / 2, y: (container.h - h) / 2, w, h };
}

export function applyView(r: Rect, v: ViewTransform): Rect {
  return { x: r.x * v.zoom + v.panX, y: r.y * v.zoom + v.panY, w: r.w * v.zoom, h: r.h * v.zoom };
}

export type MappedPoint = { nx: number; ny: number; inside: boolean };

/** Container-relative point -> normalized remote coordinate (clamped to the frame). */
export function toRemote(px: number, py: number, rendered: Rect): MappedPoint {
  if (rendered.w <= 0 || rendered.h <= 0) return { nx: 0, ny: 0, inside: false };
  const nx = (px - rendered.x) / rendered.w, ny = (py - rendered.y) / rendered.h;
  const inside = nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1;
  return { nx: clamp01(nx), ny: clamp01(ny), inside };
}

/** Normalized coordinate -> pixel on the actual desktop resolution. */
export function toDesktopPixels(nx: number, ny: number, desktop: Size) {
  return { x: Math.min(desktop.w - 1, Math.round(nx * desktop.w)), y: Math.min(desktop.h - 1, Math.round(ny * desktop.h)) };
}

/** Normalized remote coordinate -> container-relative point (for drawing a local cursor). */
export function fromRemote(nx: number, ny: number, rendered: Rect) {
  return { x: rendered.x + nx * rendered.w, y: rendered.y + ny * rendered.h };
}

/** Zoom about a focal point, keeping the content covering the container or centered when smaller. */
export function zoomAt(v: ViewTransform, nextZoom: number, fx: number, fy: number, container: Size, base?: Rect): ViewTransform {
  const zoom = Math.min(MAX_ZOOM, Math.max(1, nextZoom));
  const k = zoom / v.zoom;
  return clampPan({ zoom, panX: fx - (fx - v.panX) * k, panY: fy - (fy - v.panY) * k }, container, base);
}

export function clampPan(v: ViewTransform, c: Size, base?: Rect): ViewTransform {
  const b = base && base.w > 0 && base.h > 0 ? base : { x: 0, y: 0, w: c.w, h: c.h };
  const sw = b.w * v.zoom;
  const sh = b.h * v.zoom;

  const panX =
    sw <= c.w
      ? (c.w - sw) / 2 - b.x * v.zoom
      : Math.min(-b.x * v.zoom, Math.max(c.w - (b.x + b.w) * v.zoom, v.panX));

  const panY =
    sh <= c.h
      ? (c.h - sh) / 2 - b.y * v.zoom
      : Math.min(-b.y * v.zoom, Math.max(c.h - (b.y + b.h) * v.zoom, v.panY));

  return { zoom: v.zoom, panX, panY };
}

const clamp01 = (n: number) => Math.min(1, Math.max(0, n));
