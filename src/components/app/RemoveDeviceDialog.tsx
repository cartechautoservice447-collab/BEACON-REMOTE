import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription,
  AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { supabase } from "@/integrations/supabase/client";
import type { Device } from "@/lib/devices";

export function RemoveDeviceDialog({ device, onClose, onRemoved }: { device: Device | null; onClose: () => void; onRemoved?: () => void }) {
  const qc = useQueryClient();
  const m = useMutation({
    mutationFn: async (id: string) => {
      const { error, count } = await supabase.from("devices").delete({ count: "exact" }).eq("id", id);
      if (error) throw error;
      if (!count) throw new Error("Device not found");
    },
    onSuccess: () => {
      toast.success("Laptop removed");
      qc.invalidateQueries({ queryKey: ["devices"] });
      onClose();
      onRemoved?.();
    },
    onError: () => toast.error("Couldn't remove the laptop. Please try again."),
  });
  return (
    <AlertDialog open={!!device} onOpenChange={(o) => !o && onClose()}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Remove {device?.name}?</AlertDialogTitle>
          <AlertDialogDescription>
            This laptop will no longer be able to connect to your account. You can pair it again later.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep</AlertDialogCancel>
          <AlertDialogAction
            className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            disabled={m.isPending}
            onClick={(e) => { e.preventDefault(); if (device) m.mutate(device.id); }}
          >
            {m.isPending ? "Removing…" : "Remove"}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
