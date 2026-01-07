// ABOUTME: Form component for adding/editing custom tools.
// ABOUTME: Includes icon picker, toggle switches, and two-step delete confirmation.

import { useState, useEffect } from 'react';
import { ChevronLeft, Loader2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { ToggleRow } from '../general';
import { IconPicker } from '@/components/sidebar/IconPicker';

interface ToolFormProps {
  mode: 'add' | 'edit';
  name: string;
  command: string;
  args: string;
  icon: string | null;
  description: string;
  isShell: boolean;
  enabled: boolean;
  onNameChange: (value: string) => void;
  onCommandChange: (value: string) => void;
  onArgsChange: (value: string) => void;
  onIconChange: (value: string | null) => void;
  onDescriptionChange: (value: string) => void;
  onIsShellChange: (value: boolean) => void;
  onEnabledChange: (value: boolean) => void;
  onSave: () => void;
  onBack: () => void;
  onDelete?: () => void;
  validationError?: string;
}

export function ToolForm({
  mode,
  name,
  command,
  args,
  icon,
  description,
  isShell,
  enabled,
  onNameChange,
  onCommandChange,
  onArgsChange,
  onIconChange,
  onDescriptionChange,
  onIsShellChange,
  onEnabledChange,
  onSave,
  onBack,
  onDelete,
  validationError,
}: ToolFormProps) {
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);

  // Auto-revert confirmation after 3 seconds
  useEffect(() => {
    if (confirmingDelete) {
      const timer = setTimeout(() => {
        setConfirmingDelete(false);
      }, 3000);
      return () => clearTimeout(timer);
    }
  }, [confirmingDelete]);

  const handleDeleteClick = () => {
    setConfirmingDelete(true);
  };

  const handleCancelDelete = () => {
    setConfirmingDelete(false);
  };

  const handleConfirmDelete = async () => {
    setDeleting(true);
    try {
      onDelete?.();
    } finally {
      setDeleting(false);
      setConfirmingDelete(false);
    }
  };

  return (
    <div className="flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-2 mb-4 flex-shrink-0">
        <button
          onClick={onBack}
          className="p-1 -ml-1 rounded hover:bg-muted transition-colors"
        >
          <ChevronLeft className="h-5 w-5" />
        </button>
        <h3 className="text-lg font-semibold">
          {mode === 'add' ? 'Add Tool' : 'Edit Tool'}
        </h3>
      </div>

      <div className="flex-1 overflow-y-auto min-h-0 space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <label className="dialog-label">
            Name
            <input
              type="text"
              value={name}
              onChange={(e) => onNameChange(e.target.value)}
              placeholder="Nu Shell"
              autoFocus
            />
          </label>
          <label className="dialog-label">
            Command
            <input
              type="text"
              value={command}
              onChange={(e) => onCommandChange(e.target.value)}
              placeholder="nu"
            />
          </label>
        </div>

        <label className="dialog-label">
          Arguments (comma-separated)
          <input
            type="text"
            value={args}
            onChange={(e) => onArgsChange(e.target.value)}
            placeholder="-l, -i"
          />
        </label>

        <IconPicker value={icon} onChange={onIconChange} />

        <label className="dialog-label">
          Description
          <input
            type="text"
            value={description}
            onChange={(e) => onDescriptionChange(e.target.value)}
            placeholder="Modern shell with structured data"
          />
        </label>

        <div className="border-t" />

        <ToggleRow
          id="is-shell"
          label="This is a shell"
          description="Automatically adds -l -i flags for login/interactive mode"
          checked={isShell}
          onCheckedChange={onIsShellChange}
        />

        <div className="border-t" />

        <ToggleRow
          id="enabled"
          label="Enabled"
          description="Show this tool in the tab picker menu"
          checked={enabled}
          onCheckedChange={onEnabledChange}
        />

        {validationError && (
          <div className="text-destructive text-sm">{validationError}</div>
        )}
      </div>

      <div className="flex justify-between pt-4 flex-shrink-0 border-t border-border mt-4">
        <div>
          {mode === 'edit' && onDelete && (
            <>
              {!confirmingDelete ? (
                <Button variant="destructive" onClick={handleDeleteClick}>
                  Delete
                </Button>
              ) : (
                <div className="flex gap-2">
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={handleCancelDelete}
                    disabled={deleting}
                  >
                    Cancel
                  </Button>
                  <Button
                    variant="destructive"
                    size="sm"
                    onClick={handleConfirmDelete}
                    disabled={deleting}
                  >
                    {deleting ? (
                      <Loader2 className="animate-spin" size={14} />
                    ) : (
                      'Confirm'
                    )}
                  </Button>
                </div>
              )}
            </>
          )}
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={onBack}>
            Cancel
          </Button>
          <Button onClick={onSave}>Save</Button>
        </div>
      </div>
    </div>
  );
}
