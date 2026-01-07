// ABOUTME: Shell configuration card for tools settings.
// ABOUTME: Displays list of available shells with customization options.

import { useState, useEffect } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { Terminal, Loader2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { ShellListItem } from './ShellListItem';
import { ShellEditForm } from './ShellEditForm';
import type { ShellInfo } from '@/types/shells';
import type { ShellSettings, ShellOverride, CustomShell } from '@/types/tools';

interface ShellConfigSectionProps {
  shell: ShellSettings;
  detectedShell: string;
  onShellChange: (shell: ShellSettings) => void;
  onViewChange?: (view: 'list' | 'form') => void;
}

const makeId = () => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `shell-${Date.now()}-${Math.random().toString(16).slice(2)}`;
};

const parseList = (value: string) =>
  value.split(/[,\n]/).map((s) => s.trim()).filter(Boolean);

const joinList = (items: string[]) => items.join(', ');

type View = 'list' | 'form';
type FormMode = 'edit-detected' | 'edit-custom' | 'add-custom';

export function ShellConfigSection({
  shell,
  detectedShell: _detectedShell, // Kept for backward compatibility
  onShellChange,
  onViewChange,
}: ShellConfigSectionProps) {
  void _detectedShell; // Suppress unused warning - prop kept for API compatibility
  const [view, setView] = useState<View>('list');
  const [detectedShells, setDetectedShells] = useState<ShellInfo[]>([]);
  const [isLoading, setIsLoading] = useState(true);

  // Form state
  const [formMode, setFormMode] = useState<FormMode>('edit-detected');
  const [editingShellId, setEditingShellId] = useState<string | null>(null);
  const [formName, setFormName] = useState('');
  const [formCommand, setFormCommand] = useState('');
  const [formArgs, setFormArgs] = useState('');
  const [formIcon, setFormIcon] = useState<string | null>(null);
  const [formIsDefault, setFormIsDefault] = useState(false);
  const [formIsEnabled, setFormIsEnabled] = useState(true);
  const [validationError, setValidationError] = useState('');

  // Load detected shells on mount
  useEffect(() => {
    const loadShells = async () => {
      try {
        const shells = await invoke<ShellInfo[]>('available_shells');
        setDetectedShells(shells);
      } catch (err) {
        console.error('Failed to load shells:', err);
      } finally {
        setIsLoading(false);
      }
    };
    loadShells();
  }, []);

  // Get shell overrides and custom shells with defaults
  const shellOverrides = shell.shellOverrides ?? [];
  const customShells = shell.customShells ?? [];
  const defaultShellId = shell.defaultShellId ?? '';

  // Helper to get override for a shell
  const getOverride = (shellId: string): ShellOverride | undefined => {
    return shellOverrides.find((o) => o.shellId === shellId);
  };

  // Helper to check if a shell is enabled
  const isShellEnabled = (shellId: string): boolean => {
    const override = getOverride(shellId);
    return override ? !override.disabled : true;
  };

  // Helper to get effective args for a shell
  const getEffectiveArgs = (shellId: string, defaultArgs: string[]): string[] => {
    const override = getOverride(shellId);
    return override ? override.args : defaultArgs;
  };

  // Split detected shells by type
  const nativeShells = detectedShells.filter((s) => s.shellType === 'native');
  const wslShells = detectedShells.filter((s) => s.shellType === 'wsl');

  // Get default shell for dropdown - find by ID or use first detected
  const getDefaultShellDisplay = (): string => {
    if (defaultShellId) {
      const detected = detectedShells.find((s) => s.id === defaultShellId);
      if (detected) return detected.id;
      const custom = customShells.find((s) => s.id === defaultShellId);
      if (custom) return custom.id;
    }
    // Fall back to system default
    const systemDefault = detectedShells.find((s) => s.isDefault);
    return systemDefault?.id ?? '';
  };

  const handleDefaultChange = (id: string) => {
    onShellChange({
      ...shell,
      defaultShellId: id,
    });
  };

  const handleToggleDetected = (shellId: string, enabled: boolean) => {
    const existing = shellOverrides.find((o) => o.shellId === shellId);
    const detected = detectedShells.find((s) => s.id === shellId);

    let newOverrides: ShellOverride[];
    if (existing) {
      newOverrides = shellOverrides.map((o) =>
        o.shellId === shellId ? { ...o, disabled: !enabled } : o
      );
    } else {
      newOverrides = [
        ...shellOverrides,
        {
          shellId,
          args: detected?.args ?? [],
          disabled: !enabled,
        },
      ];
    }

    onShellChange({
      ...shell,
      shellOverrides: newOverrides,
    });
  };

  const handleToggleCustom = (shellId: string, enabled: boolean) => {
    const newCustom = customShells.map((s) =>
      s.id === shellId ? { ...s, enabled } : s
    );
    onShellChange({
      ...shell,
      customShells: newCustom,
    });
  };

  const handleEditDetected = (shellInfo: ShellInfo) => {
    const override = getOverride(shellInfo.id);
    setFormMode('edit-detected');
    setEditingShellId(shellInfo.id);
    setFormName(shellInfo.name);
    setFormCommand(shellInfo.command);
    setFormArgs(joinList(override?.args ?? shellInfo.args));
    setFormIcon(shellInfo.icon || null);
    setFormIsDefault(defaultShellId === shellInfo.id);
    setFormIsEnabled(override ? !override.disabled : true);
    setValidationError('');
    setView('form');
    onViewChange?.('form');
  };

  const handleEditCustom = (customShell: CustomShell) => {
    setFormMode('edit-custom');
    setEditingShellId(customShell.id);
    setFormName(customShell.name);
    setFormCommand(customShell.command);
    setFormArgs(joinList(customShell.args));
    setFormIcon(customShell.icon || null);
    setFormIsDefault(defaultShellId === customShell.id);
    setFormIsEnabled(customShell.enabled);
    setValidationError('');
    setView('form');
    onViewChange?.('form');
  };

  const handleAddCustom = () => {
    setFormMode('add-custom');
    setEditingShellId(null);
    setFormName('');
    setFormCommand('');
    setFormArgs('');
    setFormIcon(null);
    setFormIsDefault(false);
    setFormIsEnabled(true);
    setValidationError('');
    setView('form');
    onViewChange?.('form');
  };

  const handleBack = () => {
    setView('list');
    onViewChange?.('list');
  };

  const handleSave = () => {
    if (formMode === 'edit-detected') {
      // Update override for detected shell
      const args = parseList(formArgs);
      const existing = shellOverrides.find((o) => o.shellId === editingShellId);

      let newOverrides: ShellOverride[];
      if (existing) {
        newOverrides = shellOverrides.map((o) =>
          o.shellId === editingShellId
            ? { ...o, args, disabled: !formIsEnabled }
            : o
        );
      } else {
        newOverrides = [
          ...shellOverrides,
          {
            shellId: editingShellId!,
            args,
            disabled: !formIsEnabled,
          },
        ];
      }

      onShellChange({
        ...shell,
        shellOverrides: newOverrides,
        defaultShellId: formIsDefault ? editingShellId! : (defaultShellId === editingShellId ? '' : defaultShellId),
      });
    } else {
      // Custom shell (add or edit)
      const trimmedName = formName.trim();
      if (!trimmedName) {
        setValidationError('Shell name is required');
        return;
      }
      if (!formCommand.trim()) {
        setValidationError('Command is required');
        return;
      }

      const args = parseList(formArgs);

      if (formMode === 'add-custom') {
        const newId = makeId();
        const newShell: CustomShell = {
          id: newId,
          name: trimmedName,
          command: formCommand.trim(),
          args,
          icon: formIcon ?? '',
          enabled: formIsEnabled,
        };
        onShellChange({
          ...shell,
          customShells: [...customShells, newShell],
          defaultShellId: formIsDefault ? newId : defaultShellId,
        });
      } else {
        // Edit existing custom shell
        const newCustom = customShells.map((s) =>
          s.id === editingShellId
            ? {
                ...s,
                name: trimmedName,
                command: formCommand.trim(),
                args,
                icon: formIcon ?? '',
                enabled: formIsEnabled,
              }
            : s
        );
        onShellChange({
          ...shell,
          customShells: newCustom,
          defaultShellId: formIsDefault ? editingShellId! : (defaultShellId === editingShellId ? '' : defaultShellId),
        });
      }
    }

    setView('list');
    onViewChange?.('list');
  };

  const handleDelete = () => {
    if (editingShellId && formMode === 'edit-custom') {
      const newCustom = customShells.filter((s) => s.id !== editingShellId);
      onShellChange({
        ...shell,
        customShells: newCustom,
        defaultShellId: defaultShellId === editingShellId ? '' : defaultShellId,
      });
    }
    setView('list');
    onViewChange?.('list');
  };

  // Get all shells for dropdown
  const allShells = [
    ...detectedShells.map((s) => ({ id: s.id, name: s.name })),
    ...customShells.map((s) => ({ id: s.id, name: `${s.name} (custom)` })),
  ];

  // Get the shell being edited (for form)
  const editingDetectedShell = formMode === 'edit-detected'
    ? detectedShells.find((s) => s.id === editingShellId)
    : undefined;
  const editingCustomShell = formMode === 'edit-custom'
    ? customShells.find((s) => s.id === editingShellId)
    : undefined;

  return (
    <div className="relative">
      {/* List View */}
      {view === 'list' && (
        <Card>
          <CardHeader className="pb-4">
            <div className="flex items-center justify-between">
              <div>
                <CardTitle className="text-base flex items-center gap-2">
                  <Terminal size={16} className="text-muted-foreground" />
                  Shell Configuration
                </CardTitle>
                <CardDescription>
                  Manage available shells and their settings
                </CardDescription>
              </div>
              <Button size="sm" onClick={handleAddCustom}>
                + Add Shell
              </Button>
            </div>
          </CardHeader>
          <CardContent className="space-y-1">
            {isLoading ? (
              <div className="flex items-center justify-center py-8 text-muted-foreground">
                <Loader2 className="animate-spin mr-2" size={16} />
                Loading shells...
              </div>
            ) : (
              <>
                {/* Default Shell Selector */}
                <div className="flex items-center justify-between py-2">
                  <label className="text-sm font-medium">Default shell</label>
                  <NativeSelect
                    value={getDefaultShellDisplay()}
                    onChange={(e) => handleDefaultChange(e.target.value)}
                  >
                    <NativeSelectOption value="">Auto-detect</NativeSelectOption>
                    {allShells.map((s) => (
                      <NativeSelectOption key={s.id} value={s.id}>
                        {s.name}
                      </NativeSelectOption>
                    ))}
                  </NativeSelect>
                </div>

                {/* Native Shells */}
                {nativeShells.length > 0 && (
                  <>
                    <div className="border-t my-2" />
                    <div className="dialog-subtitle">Detected Shells</div>
                    {nativeShells.map((shellInfo, index) => (
                      <div key={shellInfo.id}>
                        {index > 0 && <div className="border-t" />}
                        <ShellListItem
                          shell={{
                            ...shellInfo,
                            args: getEffectiveArgs(shellInfo.id, shellInfo.args),
                          }}
                          isDefault={getDefaultShellDisplay() === shellInfo.id}
                          isEnabled={isShellEnabled(shellInfo.id)}
                          onToggle={(enabled) => handleToggleDetected(shellInfo.id, enabled)}
                          onClick={() => handleEditDetected(shellInfo)}
                        />
                      </div>
                    ))}
                  </>
                )}

                {/* WSL Shells */}
                {wslShells.length > 0 && (
                  <>
                    <div className="border-t my-2" />
                    <div className="dialog-subtitle">WSL Distributions</div>
                    {wslShells.map((shellInfo, index) => (
                      <div key={shellInfo.id}>
                        {index > 0 && <div className="border-t" />}
                        <ShellListItem
                          shell={{
                            ...shellInfo,
                            args: getEffectiveArgs(shellInfo.id, shellInfo.args),
                          }}
                          isDefault={getDefaultShellDisplay() === shellInfo.id}
                          isEnabled={isShellEnabled(shellInfo.id)}
                          onToggle={(enabled) => handleToggleDetected(shellInfo.id, enabled)}
                          onClick={() => handleEditDetected(shellInfo)}
                        />
                      </div>
                    ))}
                  </>
                )}

                {/* Custom Shells */}
                {customShells.length > 0 && (
                  <>
                    <div className="border-t my-2" />
                    <div className="dialog-subtitle">Custom Shells</div>
                    {customShells.map((customShell, index) => (
                      <div key={customShell.id}>
                        {index > 0 && <div className="border-t" />}
                        <ShellListItem
                          shell={customShell}
                          isDefault={getDefaultShellDisplay() === customShell.id}
                          isEnabled={customShell.enabled}
                          onToggle={(enabled) => handleToggleCustom(customShell.id, enabled)}
                          onClick={() => handleEditCustom(customShell)}
                        />
                      </div>
                    ))}
                  </>
                )}

                {detectedShells.length === 0 && customShells.length === 0 && (
                  <div className="text-muted-foreground text-sm py-3">
                    No shells detected. Add a custom shell above.
                  </div>
                )}
              </>
            )}
          </CardContent>
        </Card>
      )}

      {/* Form View */}
      {view === 'form' && (
        <div className="animate-in slide-in-from-right duration-200">
          <ShellEditForm
            mode={formMode}
            shell={editingDetectedShell}
            customShell={editingCustomShell}
            name={formName}
            command={formCommand}
            args={formArgs}
            icon={formIcon}
            isDefault={formIsDefault}
            isEnabled={formIsEnabled}
            onNameChange={setFormName}
            onCommandChange={setFormCommand}
            onArgsChange={setFormArgs}
            onIconChange={setFormIcon}
            onIsDefaultChange={setFormIsDefault}
            onIsEnabledChange={setFormIsEnabled}
            onSave={handleSave}
            onBack={handleBack}
            onDelete={formMode === 'edit-custom' ? handleDelete : undefined}
            validationError={validationError}
          />
        </div>
      )}
    </div>
  );
}
