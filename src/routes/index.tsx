import { createFileRoute, Link } from "@tanstack/react-router";
import { Laptop, ShieldCheck, Smartphone } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Logo } from "@/components/app/Logo";

export const Route = createFileRoute("/")({
  head: () => ({
    meta: [
      { title: "Beacon Remote — Control your laptop from your phone" },
      { name: "description", content: "Pair your Windows laptop with your phone and manage it from anywhere." },
      { property: "og:title", content: "Beacon Remote — Control your laptop from your phone" },
      { property: "og:description", content: "Pair your Windows laptop with your phone and manage it from anywhere." },
    ],
  }),
  component: Landing,
});

function Landing() {
  return (
    <div className="flex min-h-dvh flex-col bg-background px-5">
      <header className="mx-auto flex h-16 w-full max-w-3xl items-center justify-between">
        <Logo />
        <Button asChild variant="ghost"><Link to="/auth">Sign in</Link></Button>
      </header>
      <main className="mx-auto flex w-full max-w-3xl flex-1 flex-col justify-center py-10">
        <p className="text-sm font-medium text-primary">Your laptop, in your pocket</p>
        <h1 className="mt-3 text-4xl font-semibold leading-tight sm:text-5xl">Reach your laptop<br />from your phone.</h1>
        <p className="mt-4 max-w-md text-muted-foreground">Pair your Windows laptop once, then see it and manage it from your Android phone.</p>
        <div className="mt-8 flex flex-col gap-3 sm:flex-row">
          <Button asChild size="lg"><Link to="/auth" search={{ mode: "signup" }}>Create account</Link></Button>
          <Button asChild size="lg" variant="secondary"><Link to="/auth">I have an account</Link></Button>
        </div>
        <ul className="mt-12 grid gap-3 sm:grid-cols-3">
          {[
            { i: Smartphone, t: "Made for phones", d: "Built for Android first." },
            { i: Laptop, t: "Simple pairing", d: "QR code or 6-digit code." },
            { i: ShieldCheck, t: "Private by design", d: "Only you can see your laptops." },
          ].map(({ i: I, t, d }) => (
            <li key={t} className="rounded-xl border bg-card p-4">
              <I className="size-5 text-primary" aria-hidden />
              <p className="mt-2 font-semibold">{t}</p>
              <p className="text-sm text-muted-foreground">{d}</p>
            </li>
          ))}
        </ul>
      </main>
    </div>
  );
}
