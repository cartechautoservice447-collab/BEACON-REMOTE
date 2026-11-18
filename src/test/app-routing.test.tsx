import { QueryClient } from "@tanstack/react-query";
import { createMemoryHistory, createRouter, rootRouteId } from "@tanstack/react-router";
import { describe, expect, it, vi } from "vitest";

import { routeTree } from "@/routeTree.gen";
import { supabase } from "@/integrations/supabase/client";

// Match routes without running loaders or rendering: loaders may need a server or
// network the test run lacks, and jsdom never loads the stylesheets React waits on.
describe("App routing", () => {
  it("matches a page for / instead of falling back to not found", () => {
    const router = createRouter({ routeTree, context: { queryClient: new QueryClient() } });

    const matches = router.matchRoutes("/");

    expect(matches.at(-1)?.routeId).not.toBe(rootRouteId);
  });

  it("matches /remote/$deviceId under _authenticated", () => {
    const router = createRouter({ routeTree, context: { queryClient: new QueryClient() } });

    const matches = router.matchRoutes("/remote/device-123");

    expect(matches.at(-1)?.routeId).toBe("/_authenticated/remote/$deviceId");
  });

  it("redirects unauthenticated access on /remote/$deviceId to /auth", async () => {
    const spy = vi.spyOn(supabase.auth, "getUser").mockResolvedValue({
      data: { user: null },
      error: null,
    } as never);

    const router = createRouter({
      routeTree,
      history: createMemoryHistory({ initialEntries: ["/remote/device-123"] }),
      context: { queryClient: new QueryClient() },
    });

    await router.load();

    expect(router.state.location.pathname).toBe("/auth");
    spy.mockRestore();
  });
});
