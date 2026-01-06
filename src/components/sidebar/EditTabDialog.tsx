import { DialogShell } from './DialogShell';
import { IconPicker } from './IconPicker';

interface EditTabDialogProps {
  titleValue: string;
  commandValue: string;
  iconValue: string | null;
  onTitleChange: (value: string) => void;
  onCommandChange: (value: string) => void;
  onIconChange: (value: string | null) => void;
  onClose: () => void;
  onSave: () => void;
}

export function EditTabDialog({
  titleValue,
  commandValue,
  iconValue,
  onTitleChange,
  onCommandChange,
  onIconChange,
  onClose,
  onSave,
}: EditTabDialogProps) {
  return (
    <DialogShell
      title="Edit tab"
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
    </DialogShell>
  );
}
