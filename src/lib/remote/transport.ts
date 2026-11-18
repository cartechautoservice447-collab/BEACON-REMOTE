import type { RemoteInputEvent } from "./protocol";

export type TransportState = "disconnected" | "connecting" | "connected";

/** Boundary the future WebRTC DataChannel implementation plugs into. */
export interface RemoteTransport {
  readonly state: TransportState;
  /** Returns true only when the event actually left this device. */
  send(event: RemoteInputEvent): boolean;
  close(): void;
}

/** Used until real streaming exists: never pretends to deliver anything. */
export class NotConnectedTransport implements RemoteTransport {
  readonly state: TransportState = "disconnected";
  send(): boolean { return false; }
  close(): void {}
}

type Listener = (e: RemoteInputEvent, delivered: boolean) => void;

/** Fan-out between input engines, the transport and local UI feedback. */
export class InputBus {
  private listeners = new Set<Listener>();
  constructor(public transport: RemoteTransport) {}
  emit = (e: RemoteInputEvent) => {
    const delivered = this.transport.state === "connected" && this.transport.send(e);
    for (const l of this.listeners) l(e, delivered);
  };
  subscribe(l: Listener) { this.listeners.add(l); return () => { this.listeners.delete(l); }; }
}
