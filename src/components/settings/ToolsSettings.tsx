import { useState } from 'react';
import { ChevronLeft } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Switch } from '@/components/ui/switch';
import { IconPicker } from '@/components/sidebar/IconPicker';
import type { ToolDef, ShellSettings } from '@/types/tools';

export type ToolItem = ToolDef & { id: string; name: string };

type ToolsSettingsProps = {
  tools: ToolItem[];
  shell: ShellSettings;
  detectedShell: string;
  onToolsChange: (tools: ToolItem[]) => void;
  onShellChange: (shell: ShellSettings) => void;
  onViewChange?: (view: 'list' | 'form') => void;
};

const makeId = () => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `tool-${Date.now()}-${Math.random().toString(16).slice(2)}`;
};

const parseList = (value: string) =>
  value.split(/[,\n]/).map((s) => s.trim()).filter(Boolean);

const joinList = (items: string[]) => items.join(', ');

type View = 'list' | 'form';

export function ToolsSettings({
  tools,
  shell,
  detectedShell,
  onToolsChange,
  onShellChange,
  onViewChange,
}: ToolsSettingsProps) {
  const [view, setView] = useState<View>('list');
  const [dialogMode, setDialogMode] = useState<'add' | 'edit'>('add');
  const [editingIndex, setEditingIndex] = useState<number | null>(null);

  // Form state
  const [formName, setFormName] = useState('');
  const [formCommand, setFormCommand] = useState('');
  const [formArgs, setFormArgs] = useState('');
  const [formIcon, setFormIcon] = useState<string | null>(null);
  const [formDescription, setFormDescription] = useState('');
  const [formIsShell, setFormIsShell] = useState(false);
  const [formEnabled, setFormEnabled] = useState(true);
  const [validationError, setValidationError] = useState('');

  const handleAddClick = () => {
    setFormName('');
    setFormCommand('');
    setFormArgs('');
    setFormIcon(null);
    setFormDescription('');
    setFormIsShell(false);
    setFormEnabled(true);
    setDialogMode('add');
    setEditingIndex(null);
    setValidationError('');
    setView('form');
    onViewChange?.('form');
  };

  const handleEditClick = (index: number) => {
    const tool = tools[index];
    setFormName(tool.name);
    setFormCommand(tool.command);
    setFormArgs(joinList(tool.args));
    setFormIcon(tool.icon || null);
    setFormDescription(tool.description);
    setFormIsShell(tool.isShell);
    setFormEnabled(tool.enabled);
    setDialogMode('edit');
    setEditingIndex(index);
    setValidationError('');
    setView('form');
    onViewChange?.('form');
  };

  const handleBack = () => {
    setView('list');
    onViewChange?.('list');
  };

  const handleFormSave = () => {
    const trimmedName = formName.trim();
    if (!trimmedName) {
      setValidationError('Tool name is required');
      return;
    }
    if (!formCommand.trim()) {
      setValidationError('Command is required');
      return;
    }
    const existingNames = tools
      .filter((_, i) => i !== editingIndex)
      .map((t) => t.name.trim().toLowerCase());
    if (existingNames.includes(trimmedName.toLowerCase())) {
      setValidationError('Tool names must be unique');
      return;
    }

    const args = parseList(formArgs);

    if (dialogMode === 'add') {
      const newId = makeId();
      const newTool: ToolItem = {
        id: newId,
        name: trimmedName,
        command: formCommand.trim(),
        args,
        icon: formIcon ?? '',
        description: formDescription.trim(),
        busyPatterns: [],
        isShell: formIsShell,
        order: tools.length,
        enabled: formEnabled,
      };
      onToolsChange([...tools, newTool]);
    } else if (editingIndex !== null) {
      const updated = tools.map((t, i) =>
        i === editingIndex
          ? {
              ...t,
              name: trimmedName,
              command: formCommand.trim(),
              args,
              icon: formIcon ?? '',
              description: formDescription.trim(),
              isShell: formIsShell,
              enabled: formEnabled,
            }
          : t
      );
      onToolsChange(updated);
    }
    setView('list');
    onViewChange?.('list');
  };

  const handleFormDelete = () => {
    if (editingIndex !== null) {
      onToolsChange(tools.filter((_, i) => i !== editingIndex));
    }
    setView('list');
    onViewChange?.('list');
  };

  const handleToggle = (index: number, enabled: boolean) => {
    const updated = tools.map((t, i) => (i === index ? { ...t, enabled } : t));
    onToolsChange(updated);
  };

  return (
    <div className="relative">
      {/* List View */}
      {view === 'list' && (
        <div className="space-y-6">
          {/* Default Shell Override */}
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base">Default Shell</CardTitle>
              <CardDescription>
                Auto-detected: <code className="text-xs bg-muted px-1 py-0.5 rounded">{detectedShell}</code>
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <label className="dialog-label">
                Override Shell Path (leave empty for auto-detect)
                <input
                  type="text"
                  value={shell.defaultShell}
                  onChange={(e) => onShellChange({ ...shell, defaultShell: e.target.value })}
                  placeholder={detectedShell}
                />
              </label>
              <label className="dialog-label">
                Additional Shell Arguments (comma-separated)
                <input
                  type="text"
                  value={joinList(shell.defaultShellArgs)}
                  onChange={(e) =>
                    onShellChange({
                      ...shell,
                      defaultShellArgs: parseList(e.target.value),
                    })
                  }
                  placeholder="-NoLogo"
                />
              </label>
            </CardContent>
          </Card>

          {/* Custom Tools List */}
          <Card>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base">Custom Tools & Shells</CardTitle>
                  <CardDescription>Add custom commands to the tab picker</CardDescription>
                </div>
                <Button size="sm" onClick={handleAddClick}>
                  + Add Tool
                </Button>
              </div>
            </CardHeader>
            <CardContent className="space-y-2">
              {tools.length === 0 ? (
                <div className="text-muted-foreground text-sm">No custom tools configured</div>
              ) : (
                tools.map((tool, index) => (
                  <div
                    key={tool.id}
                    className="flex items-center justify-between p-3 rounded-md border cursor-pointer hover:bg-accent transition-colors"
                    onClick={() => handleEditClick(index)}
                  >
                    <div className="flex items-center gap-3 min-w-0">
                      {tool.icon ? (
                        <img src={tool.icon} alt="" className="w-5 h-5 shrink-0" />
                      ) : (
                        <div className="w-5 h-5 rounded bg-muted flex items-center justify-center text-xs font-medium shrink-0">
                          {tool.name.slice(0, 1).toUpperCase()}
                        </div>
                      )}
                      <div className="min-w-0">
                        <div className="font-medium truncate">{tool.name}</div>
                        <div className="text-sm text-muted-foreground truncate">
                          {tool.command}
                          {tool.isShell && (
                            <span className="ml-2 text-xs bg-muted px-1 rounded">shell</span>
                          )}
                        </div>
                      </div>
                    </div>
                    <Switch
                      checked={tool.enabled}
                      onClick={(e) => e.stopPropagation()}
                      onCheckedChange={(enabled) => handleToggle(index, enabled)}
                    />
                  </div>
                ))
              )}
            </CardContent>
          </Card>
        </div>
      )}

      {/* Form View */}
      {view === 'form' && (
        <div className="animate-in slide-in-from-right duration-200 flex flex-col min-h-0 h-full">
          <div className="flex items-center gap-2 mb-4 flex-shrink-0">
            <button
              onClick={handleBack}
              className="p-1 -ml-1 rounded hover:bg-muted transition-colors"
            >
              <ChevronLeft className="h-5 w-5" />
            </button>
            <h3 className="text-lg font-semibold">
              {dialogMode === 'add' ? 'Add Tool' : 'Edit Tool'}
            </h3>
          </div>

          <div className="flex-1 overflow-y-auto min-h-0 space-y-4">
            <div className="grid grid-cols-2 gap-4">
              <label className="dialog-label">
                Name
                <input
                  type="text"
                  value={formName}
                  onChange={(e) => setFormName(e.target.value)}
                  placeholder="Nu Shell"
                  autoFocus
                />
              </label>
              <label className="dialog-label">
                Command
                <input
                  type="text"
                  value={formCommand}
                  onChange={(e) => setFormCommand(e.target.value)}
                  placeholder="nu"
                />
              </label>
            </div>

            <label className="dialog-label">
              Arguments (comma-separated)
              <input
                type="text"
                value={formArgs}
                onChange={(e) => setFormArgs(e.target.value)}
                placeholder="-l, -i"
              />
            </label>

            <IconPicker value={formIcon} onChange={setFormIcon} />

            <label className="dialog-label">
              Description
              <input
                type="text"
                value={formDescription}
                onChange={(e) => setFormDescription(e.target.value)}
                placeholder="Modern shell with structured data"
              />
            </label>

            <div className="flex items-center gap-3 py-2">
              <Switch checked={formIsShell} onCheckedChange={setFormIsShell} />
              <div>
                <div className="font-medium text-sm">This is a shell</div>
                <div className="text-xs text-muted-foreground">
                  Automatically adds -l -i flags for login/interactive mode
                </div>
              </div>
            </div>

            <div className="flex items-center gap-3 py-2">
              <Switch checked={formEnabled} onCheckedChange={setFormEnabled} />
              <div>
                <div className="font-medium text-sm">Enabled</div>
                <div className="text-xs text-muted-foreground">
                  Show this tool in the tab picker menu
                </div>
              </div>
            </div>

            {validationError && (
              <div className="text-destructive text-sm">{validationError}</div>
            )}
          </div>

          <div className="flex justify-between pt-4 shrink-0 border-t border-border mt-4">
            <div>
              {dialogMode === 'edit' && (
                <Button variant="destructive" onClick={handleFormDelete}>
                  Delete
                </Button>
              )}
            </div>
            <div className="flex gap-2">
              <Button variant="outline" onClick={handleBack}>
                Cancel
              </Button>
              <Button onClick={handleFormSave}>Save</Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
