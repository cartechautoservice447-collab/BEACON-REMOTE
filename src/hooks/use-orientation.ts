import { useSyncExternalStore } from "react";

const query = "(orientation: portrait)";
function subscribe(cb: () => void) {
  const mq = window.matchMedia(query);
  mq.addEventListener("change", cb);
  window.addEventListener("resize", cb);
  return () => { mq.removeEventListener("change", cb); window.removeEventListener("resize", cb); };
}
/** True in portrait. Server render assumes landscape so the workspace isn't hidden behind the overlay before hydration. */
export function usePortrait() {
  return useSyncExternalStore(subscribe, () => window.matchMedia(query).matches, () => false);
}
