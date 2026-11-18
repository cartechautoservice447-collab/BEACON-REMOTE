import { queryOptions } from "@tanstack/react-query";
import { supabase } from "@/integrations/supabase/client";

export type Device = {
  id: string;
  name: string;
  os: string;
  agent_version: string;
  last_heartbeat_at: string | null;
  last_connected_at: string | null;
  registered_at: string;
};

const COLUMNS = "id, name, os, agent_version, last_heartbeat_at, last_connected_at, registered_at";

export const devicesQuery = queryOptions({
  queryKey: ["devices"],
  queryFn: async (): Promise<Device[]> => {
    const { data, error } = await supabase.from("devices").select(COLUMNS).order("registered_at", { ascending: false });
    if (error) throw error;
    return data;
  },
  refetchInterval: 30_000,
});

export const deviceQuery = (id: string) =>
  queryOptions({
    queryKey: ["devices", id],
    queryFn: async (): Promise<Device | null> => {
      const { data, error } = await supabase.from("devices").select(COLUMNS).eq("id", id).maybeSingle();
      if (error) throw error;
      return data;
    },
  });

export function formatRelative(iso: string | null): string {
  if (!iso) return "Never";
  const diff = Date.now() - Date.parse(iso);
  const m = Math.round(diff / 60000);
  if (m < 1) return "Just now";
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h ago`;
  return new Date(iso).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}
