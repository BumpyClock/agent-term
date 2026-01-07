// ABOUTME: Form component for editing shell settings.
// ABOUTME: Supports both detected shells (read-only name) and custom shells (fully editable).

import { useState, useEffect } from 'react';
import { ChevronLeft, Loader2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { ToggleRow } from '../general';
import { IconPicker } from '@/components/sidebar/IconPicker';
import type { ShellInfo } from '@/types/shells';
import type { CustomShell } from '@/types/tools';

interface ShellEditFormProps {
  mode: 'edit-detected' | 'edit-custom' | 'add-custom';
  // For detected shells
  shell?: ShellInfo;
  // For custom shells
  customShell?: CustomShell;
  // Form values
  name: string;
  command: string;
  args: string;
  icon: string | null;
  isDefault: boolean;
  isEnabled: boolean;
  // Handlers
  onNameChange: (value: string) => void;
  onCommandChange: (value: string) => void;
  onArgsChange: (value: string) => void;
  onIconChange: (value: string | null) => void;
  onIsDefaultChange: (value: boolean) => void;
  onIsEnabledChange: (value: boolean) => void;
  onSave: () => void;
  onBack: () => void;
  onDelete?: () => void;
  validationError?: string;
}

export function ShellEditForm({
  mode,
  shell,
  customShell: _customShell, // Available for future use
  name,
  command,
  args,
  icon,
  isDefault,
  isEnabled,
  onNameChange,
  onCommandChange,
  onArgsChange,
  onIconChange,
  onIsDefaultChange,
  onIsEnabledChange,
  onSave,
  onBack,
  onDelete,
  validationError,
}: ShellEditFormProps) {
  void _customShell; // Suppress unused warning - available for future custom shell info display

  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const isDetected = mode === 'edit-detected';
  const isCustom = mode === 'edit-custom' || mode === 'add-custom';
  const isAdd = mode === 'add-custom';

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

  const getTitle = () => {
    if (isAdd) return 'Add Shell';
    if (isDetected) return 'Edit Shell';
    return 'Edit Custom Shell';
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
        <h3 className="text-lg font-semibold">{getTitle()}</h3>
      </div>

      <div className="flex-1 overflow-y-auto min-h-0 space-y-4">
        {/* Shell info header for detected shells */}
        {isDetected && shell && (
          <div className="flex items-center gap-3 p-3 rounded-lg border bg-muted/30">
            {shell.icon ? (
              <img src={shell.icon} alt="" className="w-8 h-8 shrink-0 object-contain" />
            ) : (
              <div className="w-8 h-8 rounded bg-muted flex items-center justify-center text-sm font-medium shrink-0">
                {shell.name.slice(0, 1).toUpperCase()}
              </div>
            )}
            <div className="min-w-0">
              <div className="font-medium">{shell.name}</div>
              <div className="text-xs text-muted-foreground truncate font-mono">
                {shell.command}
              </div>
            </div>
          </div>
        )}

        {/* Editable fields for custom shells */}
        {isCustom && (
          <>
            <div className="grid grid-cols-2 gap-4">
              <label className="dialog-label">
                Name
                <input
                  type="text"
                  value={name}
                  onChange={(e) => onNameChange(e.target.value)}
                  placeholder="Nu Shell"
                  autoFocus={isAdd}
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

            <IconPicker value={icon} onChange={onIconChange} />
          </>
        )}

        {/* Arguments field - always editable */}
        <label className="dialog-label">
          Launch Arguments (comma-separated)
          <input
            type="text"
            value={args}
            onChange={(e) => onArgsChange(e.target.value)}
            placeholder="-NoLogo, -NoProfile"
            autoFocus={isDetected}
          />
        </label>

        <div className="border-t" />

        <ToggleRow
          id="is-default"
          label="Set as default"
          description="New terminals will use this shell by default"
          checked={isDefault}
          onCheckedChange={onIsDefaultChange}
        />

        <div className="border-t" />

        <ToggleRow
          id="is-enabled"
          label="Enabled"
          description="Show this shell in the tab picker"
          checked={isEnabled}
          onCheckedChange={onIsEnabledChange}
        />

        {validationError && (
          <div className="text-destructive text-sm">{validationError}</div>
        )}
      </div>

      <div className="flex justify-between pt-4 flex-shrink-0 border-t border-border mt-4">
        <div>
          {!isAdd && isCustom && onDelete && (
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
