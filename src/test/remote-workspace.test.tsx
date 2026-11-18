import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { createMemoryHistory, createRouter, RouterProvider } from "@tanstack/react-router";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DeviceCard } from "@/components/app/DeviceCard";
import { FloatingToolbar } from "@/components/remote/FloatingToolbar";
import { VirtualKeyboard } from "@/components/remote/VirtualKeyboard";
import {
  createMockSupabaseAdminClient,
  createMockSupabaseClient,
  decodeMockJwtClaims,
  isMockAuthAllowed,
} from "@/integrations/supabase/mock-client";
import type { Device } from "@/lib/devices";
import { KeyboardEngine } from "@/lib/remote/keyboard-engine";
import { createLayout, defaultFull, defaultPrefs, prefsKey, savePrefs } from "@/lib/remote/keyboard-layout";
import type { RemoteInputEvent } from "@/lib/remote/protocol";
import { InputBus, NotConnectedTransport } from "@/lib/remote/transport";
import { routeTree } from "@/routeTree.gen";
import { RemoteWorkspace } from "@/routes/_authenticated/remote.$deviceId";

const { mockGetUser, mockFrom } = vi.hoisted(() => ({
  mockGetUser: vi.fn(),
  mockFrom: vi.fn(),
}));

vi.mock("@/integrations/supabase/client", () => ({
  supabase: {
    auth: {
      getUser: mockGetUser,
      getSession: vi.fn().mockResolvedValue({ data: { session: null }, error: null }),
      onAuthStateChange: vi.fn(() => ({
        data: { subscription: { unsubscribe: vi.fn() } },
      })),
      signOut: vi.fn().mockResolvedValue({ error: null }),
    },
    from: mockFrom,
  },
}));

const SAMPLE_DEVICE: Device = {
  id: "11111111-1111-4111-8111-111111111111",
  name: "Test Laptop",
  os: "Windows 11",
  agent_version: "0.1.0",
  last_heartbeat_at: null,
  last_connected_at: null,
  registered_at: "2026-01-01T00:00:00.000Z",
};

function renderWithQuery(ui: React.ReactNode) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

async function renderAuthedRoute(initialPath: string, deviceResult: { data: Device | null; error: unknown }) {
  mockGetUser.mockResolvedValue({
    data: {
      user: {
        id: "user-test-1",
        email: "tester@example.invalid",
      },
    },
    error: null,
  });

  mockFrom.mockImplementation(() => {
    const chain = {
      select: () => chain,
      eq: () => chain,
      order: () => Promise.resolve({ data: deviceResult.data ? [deviceResult.data] : [], error: null }),
      maybeSingle: () => Promise.resolve(deviceResult),
    };
    return chain;
  });

  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: [initialPath] }),
    context: { queryClient: qc },
  });
  await router.load();
  const utils = render(
    <QueryClientProvider client={qc}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { ...utils, router };
}

describe("Phase 2 remote workspace & navigation", () => {
  beforeEach(() => {
    localStorage.clear();
    mockGetUser.mockReset();
    mockFrom.mockReset();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("DeviceCard Connect button navigates to /remote/$deviceId with honest notice", async () => {
    const { router } = await renderAuthedRoute("/dashboard", {
      data: SAMPLE_DEVICE,
      error: null,
    });
    const connectLink = await screen.findByRole("link", { name: /^connect$/i });
    expect(connectLink).toHaveAttribute("href", `/remote/${SAMPLE_DEVICE.id}`);
    expect(screen.getByText(/live screen streaming and remote input delivery are not yet connected/i)).toBeInTheDocument();
    fireEvent.click(connectLink);
    await waitFor(() => {
      expect(router.state.location.pathname).toBe(`/remote/${SAMPLE_DEVICE.id}`);
    });
    await waitFor(() => {
      expect(screen.getByText(`No screen from ${SAMPLE_DEVICE.name}`)).toBeInTheDocument();
    });
    expect(DeviceCard).toBeDefined();
  });

  it("rejects unauthorized or missing device ownership on /remote/$deviceId", async () => {
    await renderAuthedRoute("/remote/99999999-9999-4999-8999-999999999999", {
      data: null,
      error: null,
    });

    await waitFor(() => {
      expect(screen.getByText(/this laptop isn't linked to your account/i)).toBeInTheDocument();
    });
    expect(screen.queryByTestId("remote-surface")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /back to devices/i })).toHaveAttribute("href", "/devices");
  });

  it("handles invalid device ID errors safely without rendering the remote screen", async () => {
    await renderAuthedRoute("/remote/invalid-id", {
      data: null,
      error: new Error("invalid input syntax for type uuid"),
    });

    await waitFor(() => {
      expect(screen.getByText(/this laptop isn't linked to your account/i)).toBeInTheDocument();
    });
    expect(screen.queryByTestId("remote-surface")).not.toBeInTheDocument();
  });

  it("hydrates saved keyboard preferences immediately on initial workspace render without default flash", async () => {
    const customPrefs = createLayout(defaultPrefs(), "My Custom", defaultFull());
    savePrefs(localStorage, "user-test-1", customPrefs);
    const setItemSpy = vi.spyOn(Storage.prototype, "setItem");

    await renderAuthedRoute(`/remote/${SAMPLE_DEVICE.id}`, {
      data: SAMPLE_DEVICE,
      error: null,
    });

    await waitFor(() => {
      expect(screen.getByText(`No screen from ${SAMPLE_DEVICE.name}`)).toBeInTheDocument();
    });

    // Opening the keyboard must show the saved custom layout immediately, without overwriting storage
    fireEvent.click(screen.getByRole("button", { name: "Show keyboard" }));
    expect(screen.getByRole("group", { name: /my custom/i })).toBeInTheDocument();
    expect(setItemSpy).not.toHaveBeenCalledWith(prefsKey("user-test-1"), expect.anything());
  });

  it("renders honest no-stream placeholder and switches between Mouse and Touch modes", () => {
    renderWithQuery(
      <RemoteWorkspace device={SAMPLE_DEVICE} userId="user-test-1" onDisconnectDone={() => {}} />,
    );

    expect(screen.getByText(`No screen from ${SAMPLE_DEVICE.name}`)).toBeInTheDocument();
    expect(screen.getByText(/live screen sharing isn't available yet/i)).toBeInTheDocument();
    expect(screen.getByText("Not connected")).toBeInTheDocument();

    const mouseRadio = screen.getByRole("radio", { name: "Mouse Mode" });
    const touchRadio = screen.getByRole("radio", { name: "Touch Mode" });
    expect(mouseRadio).toHaveAttribute("aria-checked", "true");
    expect(touchRadio).toHaveAttribute("aria-checked", "false");

    fireEvent.click(touchRadio);
    expect(mouseRadio).toHaveAttribute("aria-checked", "false");
    expect(touchRadio).toHaveAttribute("aria-checked", "true");
  });

  it("toggles virtual keyboard and opens keyboard customization editor", async () => {
    renderWithQuery(
      <RemoteWorkspace device={SAMPLE_DEVICE} userId="user-test-1" onDisconnectDone={() => {}} />,
    );

    const kbBtn = screen.getByRole("button", { name: "Show keyboard" });
    fireEvent.click(kbBtn);
    expect(screen.getByRole("group", { name: /full keyboard/i })).toBeInTheDocument();

    // Open More panel -> Customize keyboard
    fireEvent.click(screen.getByRole("button", { name: "More controls" }));
    fireEvent.click(screen.getByRole("button", { name: /customize keyboard/i }));

    const dialog = screen.getByRole("dialog");
    expect(dialog).toBeInTheDocument();
    expect(within(dialog).getByText("Customize keyboard")).toBeInTheDocument();

    // Remove PrtSc key from layout and verify persistence
    const rows = within(dialog).getByLabelText("Keyboard rows");
    fireEvent.click(within(rows).getByText("PrtSc"));
    fireEvent.click(within(dialog).getByText("Remove"));
    expect(within(dialog).getByLabelText("Add PrtSc")).toBeInTheDocument();

    // Close dialog before toggling keyboard visibility
    fireEvent.click(within(dialog).getByText("Close"));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    // Hide keyboard
    fireEvent.click(screen.getByLabelText("Hide keyboard"));
    expect(screen.queryByRole("group", { name: /full keyboard/i })).not.toBeInTheDocument();
  });

  it("releases held modifiers and emits input.reset on blur, visibilitychange, and keyboard hide", () => {
    const emitted: RemoteInputEvent[] = [];

    renderWithQuery(
      <RemoteWorkspace
        device={SAMPLE_DEVICE}
        userId="user-test-1"
        onDisconnectDone={() => {}}
        onBus={(bus) => {
          bus.subscribe((e) => {
            emitted.push(e);
          });
        }}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Show keyboard" }));
    const shiftBtn = screen.getByRole("button", { name: "Shift" });
    fireEvent.pointerDown(shiftBtn, { pointerId: 1 });
    expect(shiftBtn).toHaveAttribute("aria-pressed", "true");

    // Simulate browser losing focus
    emitted.length = 0;
    act(() => {
      window.dispatchEvent(new Event("blur"));
    });
    expect(shiftBtn).toHaveAttribute("aria-pressed", "false");
    expect(emitted).toEqual([
      { type: "key", code: "ShiftLeft", action: "up" },
      { type: "input.reset" },
    ]);

    // Re-activate modifier and simulate tab hidden
    fireEvent.pointerDown(shiftBtn, { pointerId: 2 });
    expect(shiftBtn).toHaveAttribute("aria-pressed", "true");
    emitted.length = 0;
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    act(() => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expect(shiftBtn).toHaveAttribute("aria-pressed", "false");
    expect(emitted).toEqual([
      { type: "key", code: "ShiftLeft", action: "up" },
      { type: "input.reset" },
    ]);
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });

    // Re-activate modifier, hide keyboard, and verify modifier was released when re-opened
    fireEvent.pointerDown(shiftBtn, { pointerId: 3 });
    expect(shiftBtn).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(screen.getByRole("button", { name: "Hide keyboard" }));
    fireEvent.click(screen.getByRole("button", { name: "Show keyboard" }));
    expect(screen.getByRole("button", { name: "Shift" })).toHaveAttribute("aria-pressed", "false");
  });

  it("performs idempotent disconnect cleanup, emits input.reset before transport close, and ignores window resize", () => {
    const origFullscreenEl = Object.getOwnPropertyDescriptor(document, "fullscreenElement");
    const origExitFullscreen = Object.getOwnPropertyDescriptor(document, "exitFullscreen");
    const exitSpy = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(document, "fullscreenElement", { configurable: true, value: document.documentElement });
    Object.defineProperty(document, "exitFullscreen", { configurable: true, value: exitSpy });
    const onDisconnectDone = vi.fn();

    try {
      const calls: string[] = [];
      const sendSpy = vi.spyOn(NotConnectedTransport.prototype, "send");
      vi.spyOn(NotConnectedTransport.prototype, "close").mockImplementation(() => {
        calls.push("transport:close");
      });

      renderWithQuery(
        <RemoteWorkspace
          device={SAMPLE_DEVICE}
          userId="user-test-1"
          onDisconnectDone={onDisconnectDone}
          onBus={(bus) => {
            bus.subscribe((e, delivered) => {
              calls.push(`emit:${e.type}:${delivered}`);
            });
          }}
        />,
      );

      // Simulating software-keyboard viewport resize must NOT exit fullscreen
      act(() => {
        window.dispatchEvent(new Event("resize"));
      });
      expect(exitSpy).not.toHaveBeenCalled();

      // Explicit disconnect triggers input.reset before transport.close(), once
      const disconnectBtn = screen.getByRole("button", { name: "Disconnect" });
      fireEvent.click(disconnectBtn);
      fireEvent.click(disconnectBtn);
      expect(exitSpy).toHaveBeenCalledTimes(1);
      expect(onDisconnectDone).toHaveBeenCalledTimes(1);
      expect(calls).toEqual(["emit:input.reset:false", "transport:close"]);
      expect(sendSpy).not.toHaveBeenCalled();
    } finally {
      if (origFullscreenEl) Object.defineProperty(document, "fullscreenElement", origFullscreenEl);
      else delete (document as unknown as Record<string, unknown>)["fullscreenElement"];
      if (origExitFullscreen) Object.defineProperty(document, "exitFullscreen", origExitFullscreen);
      else delete (document as unknown as Record<string, unknown>)["exitFullscreen"];
    }
  });

  it("releases a normal VirtualKeyboard key on lostpointercapture without pointerup", () => {
    const events: RemoteInputEvent[] = [];
    const engine = new KeyboardEngine((e) => events.push(e));
    render(<VirtualKeyboard layout={defaultFull()} engine={engine} />);

    const aBtn = screen.getByRole("button", { name: "A" });
    fireEvent.pointerDown(aBtn, { pointerId: 1 });
    expect(engine.heldCodes).toEqual(["KeyA"]);

    fireEvent.lostPointerCapture(aBtn, { pointerId: 1 });
    expect(events).toEqual([
      { type: "key", code: "KeyA", action: "down" },
      { type: "key", code: "KeyA", action: "up" },
    ]);
    expect(engine.heldCodes).toEqual([]);

    // Subsequent pointerup/cancel must not emit duplicate key.up events
    fireEvent.pointerUp(aBtn, { pointerId: 1 });
    expect(events).toHaveLength(2);
  });

  it("constrains VirtualKeyboard height on low-height landscape viewports with internal scroll", () => {
    const engine = new KeyboardEngine(() => {});
    render(<VirtualKeyboard layout={defaultFull()} engine={engine} />);

    const group = screen.getByRole("group", { name: /full keyboard/i });
    expect(group.className).toContain("max-h-[46dvh]");
    expect(group.className).toContain("overflow-y-auto");

    const aBtn = screen.getByRole("button", { name: "A" });
    expect(aBtn.className).toContain("h-7");
    expect(aBtn.className).not.toMatch(/(^|\s)sm:h-10(\s|$)/);
    expect(aBtn.className).toContain("[@media(min-height:540px)]:h-8");
  });

  it("preserves FloatingToolbar position across re-renders and re-clamps on workspace resize", () => {
    let roCallback: ResizeObserverCallback | null = null;
    const origResizeObserver = window.ResizeObserver;
    window.ResizeObserver = class MockResizeObserver {
      constructor(cb: ResizeObserverCallback) {
        roCallback = cb;
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    };

    try {
      let parentW = 800;
      let parentH = 400;
      const baseProps = {
        mode: "mouse" as const,
        onMode: () => {},
        keyboardOpen: false,
        onKeyboard: () => {},
        fit: "fit" as const,
        onFit: () => {},
        fullscreen: false,
        fullscreenAvailable: true,
        onFullscreen: () => {},
        onMore: () => {},
        onDisconnect: () => {},
        collapsed: false,
        onCollapse: () => {},
        connection: "disconnected" as const,
      };

      const { rerender } = render(
        <div data-testid="toolbar-parent">
          <FloatingToolbar {...baseProps} />
        </div>,
      );

      const parent = screen.getByTestId("toolbar-parent");
      const toolbar = screen.getByRole("toolbar", { name: "Remote controls" });
      Object.defineProperty(parent, "clientWidth", { configurable: true, get: () => parentW });
      Object.defineProperty(parent, "clientHeight", { configurable: true, get: () => parentH });
      Object.defineProperty(toolbar, "offsetWidth", { configurable: true, get: () => 300 });
      Object.defineProperty(toolbar, "offsetHeight", { configurable: true, get: () => 48 });

      const dispatchPointer = (el: HTMLElement, type: string, pointerId: number, clientX: number, clientY: number) => {
        const ev = Object.assign(new Event(type, { bubbles: true, cancelable: true }), {
          pointerId,
          clientX,
          clientY,
        });
        act(() => {
          el.dispatchEvent(ev);
        });
      };

      const grip = screen.getByRole("button", { name: "Move toolbar" });
      dispatchPointer(grip, "pointerdown", 1, 0, 0);
      dispatchPointer(grip, "pointermove", 1, 60, 40);
      dispatchPointer(grip, "pointerup", 1, 60, 40);
      expect(toolbar.style.transform).toBe("translate3d(calc(-50% + 60px), 40px, 0)");

      // Re-rendering (e.g. keyboard open) must preserve dragged position
      rerender(
        <div data-testid="toolbar-parent">
          <FloatingToolbar {...baseProps} keyboardOpen />
        </div>,
      );
      expect(toolbar.style.transform).toBe("translate3d(calc(-50% + 60px), 40px, 0)");

      // Shrinking workspace re-clamps position inside bounds (maxX=(340-300)/2=20, maxY=100-48-16=36)
      parentW = 340;
      parentH = 100;
      act(() => {
        roCallback?.([], {} as ResizeObserver);
      });
      expect(toolbar.style.transform).toBe("translate3d(calc(-50% + 20px), 36px, 0)");

      // Next drag starts cleanly from the clamped displayed position (20, 36) without jumping
      dispatchPointer(grip, "pointerdown", 2, 100, 100);
      dispatchPointer(grip, "pointermove", 2, 90, 94);
      dispatchPointer(grip, "pointerup", 2, 90, 94);
      expect(toolbar.style.transform).toBe("translate3d(calc(-50% + 10px), 30px, 0)");
    } finally {
      window.ResizeObserver = origResizeObserver;
    }
  });

  it("enforces per-user isolation on devices list and detail queries in mock client", async () => {
    const client = createMockSupabaseClient();

    // 1. Create mock user A
    const signUpA = await client.auth.signUp({ email: "user-a@example.invalid", password: "test-password-a" });
    expect(signUpA.error).toBeNull();
    const userA = signUpA.data.user!;

    // Client insert on devices must remain blocked (matching DB grants)
    const clientInsert = await client.from("devices").insert({ user_id: userA.id, name: "Blocked" });
    expect(clientInsert.error).not.toBeNull();

    // 2. Seed a confirmed device owned by user A (simulating server confirm_pairing)
    const deviceA = {
      id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
      user_id: userA.id,
      name: "User A Laptop",
      os: "Windows 11",
      agent_version: "0.1.0",
      status: "offline",
      last_heartbeat_at: null,
      last_connected_at: null,
      registered_at: "2026-01-01T00:00:00.000Z",
      updated_at: "2026-01-01T00:00:00.000Z",
    };
    localStorage.setItem("beacon_mock_supabase_devices", JSON.stringify([deviceA]));

    // User A can read own device in list and detail queries
    const ownList = await client.from("devices").select("*");
    expect(ownList.data).toHaveLength(1);
    const ownDetail = await client.from("devices").select("*").eq("id", deviceA.id).maybeSingle();
    expect((ownDetail.data as { id?: string } | null)?.id).toBe(deviceA.id);

    // 3. Sign out
    await client.auth.signOut();

    // 4. Authenticate as mock user B
    const signUpB = await client.auth.signUp({ email: "user-b@example.invalid", password: "test-password-b" });
    expect(signUpB.error).toBeNull();

    // 5-7. Query user A's device list and detail as user B -> no data returned
    const listAsB = await client.from("devices").select("*");
    expect(listAsB.data).toEqual([]);
    const detailAsB = await client.from("devices").select("*").eq("id", deviceA.id).maybeSingle();
    expect(detailAsB.data).toBeNull();

    // Browser environment cannot obtain admin mock bypass
    expect(() => createMockSupabaseAdminClient()).toThrow(/restricted to server environments/i);
  });

  it("shows RotateOverlay in portrait, allows dismissing, and handles missing fullscreen gracefully", async () => {
    const origMatchMedia = window.matchMedia;
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: (query: string) => ({
        matches: query === "(orientation: portrait)",
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      }),
    });
    Object.defineProperty(document, "fullscreenEnabled", { configurable: true, value: false });
    Object.defineProperty(document, "webkitFullscreenEnabled", { configurable: true, value: false });

    try {
      renderWithQuery(
        <RemoteWorkspace device={SAMPLE_DEVICE} userId="user-test-1" onDisconnectDone={() => {}} />,
      );

      const overlay = screen.getByRole("dialog", { name: "Rotate your phone" });
      expect(overlay).toBeInTheDocument();

      // Clicking "Go full screen & rotate" when fullscreen isn't supported does not throw
      await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: /go full screen & rotate/i }));
      });
      expect(screen.getByRole("dialog", { name: "Rotate your phone" })).toBeInTheDocument();

      // Clicking "Continue in portrait" dismisses the overlay and reveals usable workspace
      fireEvent.click(screen.getByRole("button", { name: /continue in portrait/i }));
      expect(screen.queryByRole("dialog", { name: "Rotate your phone" })).not.toBeInTheDocument();
      expect(screen.getByTestId("remote-surface")).toBeInTheDocument();
      expect(screen.getByRole("toolbar", { name: "Remote controls" })).toBeInTheDocument();
    } finally {
      Object.defineProperty(window, "matchMedia", { configurable: true, writable: true, value: origMatchMedia });
    }
  });

  it("blocks mock Supabase auth fallback when NODE_ENV is production", () => {
    const originalEnv = process.env["NODE_ENV"];
    process.env["NODE_ENV"] = "production";
    try {
      expect(isMockAuthAllowed()).toBe(false);
      expect(() => createMockSupabaseClient()).toThrow(/disabled in production/i);
      expect(() => decodeMockJwtClaims("a.b.c")).toThrow(/disabled in production/i);
    } finally {
      process.env["NODE_ENV"] = originalEnv;
    }
  });
});
