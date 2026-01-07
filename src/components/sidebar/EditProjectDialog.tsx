import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { IconPicker } from './IconPicker';

interface EditProjectDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  nameValue: string;
  pathValue: string;
  iconValue: string | null;
  onNameChange: (value: string) => void;
  onPathChange: (value: string) => void;
  onIconChange: (value: string | null) => void;
  onSave: () => void;
}

export function EditProjectDialog({
  open,
  onOpenChange,
  nameValue,
  pathValue,
  iconValue,
  onNameChange,
  onPathChange,
  onIconChange,
  onSave,
}: EditProjectDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-[400px]">
        <DialogHeader>
          <DialogTitle>Edit project</DialogTitle>
        </DialogHeader>
        <div className="grid gap-4 py-2">
          <label className="dialog-label">
            Name
            <input
              type="text"
              value={nameValue}
              onChange={(event) => onNameChange(event.target.value)}
              autoFocus
            />
          </label>
          <label className="dialog-label">
            Working directory
            <input
              type="text"
              value={pathValue}
              onChange={(event) => onPathChange(event.target.value)}
              placeholder="e.g. /Users/you/project"
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
