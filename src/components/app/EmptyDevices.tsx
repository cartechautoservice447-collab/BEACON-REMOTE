import { Link } from "@tanstack/react-router";
import { Download, Laptop, QrCode, Smartphone } from "lucide-react";
import { Button } from "@/components/ui/button";

export function EmptyDevices() {
  const steps = [
    { icon: Download, text: "Install the Beacon companion app on your Windows laptop." },
    { icon: QrCode, text: "Tap “Pair New Device” here to get a QR code and a 6-digit code." },
    { icon: Laptop, text: "Scan the QR code or type the code into the laptop app." },
  ];
  return (
    <section className="rounded-2xl border border-dashed bg-card/50 p-6 text-center">
      <span className="mx-auto grid size-14 place-items-center rounded-2xl bg-accent text-accent-foreground">
        <Smartphone className="size-7" aria-hidden />
      </span>
      <h2 className="mt-4 text-lg font-semibold">Connect your first laptop</h2>
      <p className="mx-auto mt-1 max-w-sm text-sm text-muted-foreground">No laptops are linked to your account yet. It takes about a minute.</p>
      <ol className="mx-auto mt-5 max-w-sm space-y-3 text-left">
        {steps.map((s, i) => (
          <li key={i} className="flex gap-3 text-sm">
            <span className="grid size-7 shrink-0 place-items-center rounded-full bg-secondary text-xs font-semibold">{i + 1}</span>
            <span className="pt-1 text-muted-foreground">{s.text}</span>
          </li>
        ))}
      </ol>
      <p className="mx-auto mt-4 max-w-sm text-xs text-muted-foreground">The Windows companion app is still in development, so pairing can't finish yet.</p>
      <Button asChild className="mt-5 w-full max-w-sm" size="lg"><Link to="/pair">Pair New Device</Link></Button>
    </section>
  );
}
