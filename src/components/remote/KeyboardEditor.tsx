import { useState } from "react";
import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, Plus, RotateCcw, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { KEY_BY_ID } from "@/lib/remote/keys";
import { activeLayout, addKey, availableKeys, createLayout, deleteLayout, moveKey, removeKey, restoreDefault, updateLayout, type KeyboardPrefs } from "@/lib/remote/keyboard-layout";
import { cn } from "@/lib/utils";

type Props = { open: boolean; onOpenChange: (o: boolean) => void; prefs: KeyboardPrefs; onChange: (p: KeyboardPrefs) => void };

/** Changes are saved immediately. */
export function KeyboardEditor({ open, onOpenChange, prefs, onChange }: Props) {
  const layout = activeLayout(prefs);
  const [selected, setSelected] = useState<string | null>(null);
  const [targetRow, setTargetRow] = useState(0);
  const [newName, setNewName] = useState("");
  const set = (l: typeof layout) => onChange(updateLayout(prefs, l));
  const pos = selected ? layout.rows.findIndex((r) => r.includes(selected)) : -1;
  const idx = pos >= 0 && selected ? layout.rows[pos]!.indexOf(selected) : -1;
  const builtIn = layout.id === "full" || layout.id === "compact";
  const spare = availableKeys(layout);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[92dvh] max-w-3xl overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Customize keyboard</DialogTitle>
          <DialogDescription>Tap a key to move or remove it. Changes save automatically on this phone.</DialogDescription>
        </DialogHeader>

        <div className="flex flex-wrap items-center gap-2">
          <Select value={layout.id} onValueChange={(v) => { setSelected(null); onChange({ ...prefs, activeId: v }); }}>
            <SelectTrigger className="h-10 w-48" aria-label="Layout"><SelectValue /></SelectTrigger>
            <SelectContent>{prefs.layouts.map((l) => <SelectItem key={l.id} value={l.id}>{l.name}</SelectItem>)}</SelectContent>
          </Select>
          <Button variant="secondary" size="sm" onClick={() => { set(restoreDefault(layout)); setSelected(null); }}><RotateCcw /> Restore default</Button>
          {!builtIn && <Button variant="secondary" size="sm" onClick={() => onChange(deleteLayout(prefs, layout.id))}><Trash2 /> Delete layout</Button>}
        </div>
        <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); onChange(createLayout(prefs, newName, layout)); setNewName(""); setSelected(null); }}>
          <Input value={newName} maxLength={40} onChange={(e) => setNewName(e.target.value)} placeholder="New layout name" aria-label="New layout name" className="h-10" />
          <Button type="submit" variant="secondary" className="h-10"><Plus /> Save as new</Button>
        </form>

        <div className="space-y-1 rounded-lg border bg-muted/40 p-2" aria-label="Keyboard rows">
          {layout.rows.map((row, ri) => (
            <div key={ri} className="flex flex-wrap gap-1">
              <span className="w-5 self-center text-[10px] text-muted-foreground">{ri + 1}</span>
              {row.map((id) => (
                <button key={id} type="button" onClick={() => setSelected(id === selected ? null : id)} aria-pressed={id === selected}
                  className={cn("h-8 min-w-8 rounded border bg-card px-1.5 text-xs", id === selected && "border-primary bg-primary text-primary-foreground")}>
                  {KEY_BY_ID[id]?.label}
                </button>
              ))}
            </div>
          ))}
        </div>

        {selected && pos >= 0 && (
          <div className="flex flex-wrap items-center gap-2 rounded-lg border p-2">
            <span className="text-sm font-medium">“{KEY_BY_ID[selected]?.label}”</span>
            <Button size="icon" variant="secondary" aria-label="Move left" disabled={idx === 0} onClick={() => set(moveKey(layout, selected, pos, idx - 1))}><ArrowLeft /></Button>
            <Button size="icon" variant="secondary" aria-label="Move right" disabled={idx === layout.rows[pos]!.length - 1} onClick={() => set(moveKey(layout, selected, pos, idx + 1))}><ArrowRight /></Button>
            <Button size="icon" variant="secondary" aria-label="Move up a row" disabled={pos === 0} onClick={() => set(moveKey(layout, selected, pos - 1, idx))}><ArrowUp /></Button>
            <Button size="icon" variant="secondary" aria-label="Move down a row" onClick={() => set(moveKey(layout, selected, pos + 1, idx))}><ArrowDown /></Button>
            <Button size="sm" variant="destructive" onClick={() => { set(removeKey(layout, selected)); setSelected(null); }}><Trash2 /> Remove</Button>
          </div>
        )}

        <div className="space-y-2">
          <div className="flex items-center gap-2">
            <p className="text-sm font-medium">Add or restore keys</p>
            <Select value={String(targetRow)} onValueChange={(v) => setTargetRow(Number(v))}>
              <SelectTrigger className="h-8 w-28" aria-label="Add to row"><SelectValue /></SelectTrigger>
              <SelectContent>{[...layout.rows, []].map((_, i) => <SelectItem key={i} value={String(i)}>{i < layout.rows.length ? `Row ${i + 1}` : "New row"}</SelectItem>)}</SelectContent>
            </Select>
          </div>
          {spare.length === 0 ? <p className="text-xs text-muted-foreground">Every key is already on this layout.</p> : (
            <div className="flex flex-wrap gap-1">
              {spare.map((id) => (
                <button key={id} type="button" onClick={() => set(addKey(layout, targetRow, id))} className="h-8 min-w-8 rounded border border-dashed px-1.5 text-xs hover:bg-secondary" aria-label={`Add ${KEY_BY_ID[id]?.label}`}>
                  + {KEY_BY_ID[id]?.label}
                </button>
              ))}
            </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
