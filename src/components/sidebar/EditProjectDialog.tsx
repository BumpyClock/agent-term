import { DialogShell } from './DialogShell';
import { IconPicker } from './IconPicker';

interface EditProjectDialogProps {
  nameValue: string;
  pathValue: string;
  iconValue: string | null;
  onNameChange: (value: string) => void;
  onPathChange: (value: string) => void;
  onIconChange: (value: string | null) => void;
  onClose: () => void;
  onSave: () => void;
}

export function EditProjectDialog({
  nameValue,
  pathValue,
  iconValue,
  onNameChange,
  onPathChange,
  onIconChange,
  onClose,
  onSave,
}: EditProjectDialogProps) {
  return (
    <DialogShell
      title="Edit project"
      onClose={onClose}
      actions={
        <>
          <button className="dialog-secondary" onClick={onClose}>
            Cancel
          </button>
          <button className="dialog-primary" onClick={onSave}>
            Save
          </button>
        </>
      }
    >
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
    </DialogShell>
  );
}
