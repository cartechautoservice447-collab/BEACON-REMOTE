import { useEffect, useState } from "react";
import type { User } from "@supabase/supabase-js";
import { supabase } from "@/integrations/supabase/client";

export function useCurrentUser() {
  const [user, setUser] = useState<User | null>(null);
  useEffect(() => {
    let active = true;
    supabase.auth.getUser().then(({ data }) => {
      if (active) setUser(data.user);
    });
    const sub =
      typeof supabase.auth.onAuthStateChange === "function"
        ? supabase.auth.onAuthStateChange((_event, session) => {
            if (active) setUser(session?.user ?? null);
          })
        : null;
    return () => {
      active = false;
      sub?.data.subscription.unsubscribe();
    };
  }, []);
  return user;
}
