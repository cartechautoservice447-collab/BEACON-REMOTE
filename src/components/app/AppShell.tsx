import { Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { LogOut, Settings, User as UserIcon } from "lucide-react";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Button } from "@/components/ui/button";
import { Logo } from "./Logo";
import { devicesQuery } from "@/lib/devices";
import { isDeviceOnline } from "@/lib/pairing";
import { useCurrentUser } from "@/hooks/use-session";
import { useSignOut } from "@/hooks/use-sign-out";

function ConnectionSummary() {
  const { data } = useQuery(devicesQuery);
  if (!data) return null;
  const online = data.filter((d) => isDeviceOnline(d.last_heartbeat_at)).length;
  return (
    <span className="hidden items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs text-muted-foreground min-[400px]:inline-flex">
      <span className={`size-1.5 rounded-full ${online ? "bg-success" : "bg-muted-foreground/50"}`} aria-hidden />
      {online} of {data.length} online
    </span>
  );
}

export function AppShell({ title, back, children }: { title?: string; back?: ReactNode; children: ReactNode }) {
  const user = useCurrentUser();
  const signOut = useSignOut();
  return (
    <div className="min-h-dvh bg-background">
      <header className="sticky top-0 z-20 border-b bg-background/85 backdrop-blur">
        <div className="mx-auto flex h-14 max-w-3xl items-center justify-between gap-3 px-4">
          <Link to="/dashboard" aria-label="Home"><Logo /></Link>
          <div className="flex items-center gap-2">
            <ConnectionSummary />
            <Button asChild variant="ghost" size="icon" aria-label="Settings">
              <Link to="/settings"><Settings className="size-5" /></Link>
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="secondary" size="icon" className="rounded-full" aria-label="Account menu">
                  <UserIcon className="size-4" />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end" className="w-60">
                <DropdownMenuLabel className="truncate font-normal text-muted-foreground">{user?.email ?? "Signed in"}</DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuItem asChild><Link to="/settings"><Settings /> Settings</Link></DropdownMenuItem>
                <DropdownMenuItem onSelect={() => void signOut()}><LogOut /> Sign out</DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-3xl px-4 pb-16 pt-5">
        {back}
        {title && <h1 className="mb-5 text-2xl font-semibold">{title}</h1>}
        {children}
      </main>
    </div>
  );
}

export function BackLink({ to, label }: { to: "/dashboard" | "/devices" | "/settings"; label: string }) {
  return (
    <Link to={to} className="mb-3 inline-flex min-h-10 items-center text-sm text-muted-foreground hover:text-foreground">
      ← {label}
    </Link>
  );
}
