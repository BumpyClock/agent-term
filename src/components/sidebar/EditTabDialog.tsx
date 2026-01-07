import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { IconPicker } from './IconPicker';

interface EditTabDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  titleValue: string;
  commandValue: string;
  iconValue: string | null;
  onTitleChange: (value: string) => void;
  onCommandChange: (value: string) => void;
  onIconChange: (value: string | null) => void;
  onSave: () => void;
}

export function EditTabDialog({
  open,
  onOpenChange,
  titleValue,
  commandValue,
  iconValue,
  onTitleChange,
  onCommandChange,
  onIconChange,
  onSave,
}: EditTabDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-[400px]">
        <DialogHeader>
          <DialogTitle>Edit tab</DialogTitle>
        </DialogHeader>
        <div className="grid gap-4 py-2">
          <label className="dialog-label">
            Name
            <input
              type="text"
              value={titleValue}
              onChange={(event) => onTitleChange(event.target.value)}
              autoFocus
            />
          </label>
          <label className="dialog-label">
            Command
            <input
              type="text"
              value={commandValue}
              onChange={(event) => onCommandChange(event.target.value)}
              placeholder="e.g. /bin/zsh or claude"
            />
          </label>
          <IconPicker value={iconValue} onChange={onIconChange} />
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={onSave}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
