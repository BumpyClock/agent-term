// ABOUTME: Tools settings tab for configuring default shell and custom tools.
// ABOUTME: Uses extracted components for consistency with GeneralSettings.

import { useState } from 'react';
import { Wrench } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { ShellConfigSection, ToolListItem, ToolForm } from './tools';
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
        <div className="space-y-8">
          <ShellConfigSection
            shell={shell}
            detectedShell={detectedShell}
            onShellChange={onShellChange}
          />

          {/* Custom Tools List */}
          <Card>
            <CardHeader className="pb-4">
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base flex items-center gap-2">
                    <Wrench size={16} className="text-muted-foreground" />
                    Custom Tools
                  </CardTitle>
                  <CardDescription>Add custom commands to the tab picker</CardDescription>
                </div>
                <Button size="sm" onClick={handleAddClick}>
                  + Add Tool
                </Button>
              </div>
            </CardHeader>
            <CardContent className="space-y-1">
              {tools.length === 0 ? (
                <div className="text-muted-foreground text-sm py-3">
                  No custom tools yet. Add one above.
                </div>
              ) : (
                tools.map((tool, index) => (
                  <div key={tool.id}>
                    {index > 0 && <div className="border-t" />}
                    <ToolListItem
                      tool={tool}
                      onToggle={(enabled) => handleToggle(index, enabled)}
                      onClick={() => handleEditClick(index)}
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
        <div className="animate-in slide-in-from-right duration-200">
          <ToolForm
            mode={dialogMode}
            name={formName}
            command={formCommand}
            args={formArgs}
            icon={formIcon}
            description={formDescription}
            isShell={formIsShell}
            enabled={formEnabled}
            onNameChange={setFormName}
            onCommandChange={setFormCommand}
            onArgsChange={setFormArgs}
            onIconChange={setFormIcon}
            onDescriptionChange={setFormDescription}
            onIsShellChange={setFormIsShell}
            onEnabledChange={setFormEnabled}
            onSave={handleFormSave}
            onBack={handleBack}
            onDelete={dialogMode === 'edit' ? handleFormDelete : undefined}
            validationError={validationError}
          />
        </div>
      )}
    </div>
  );
}
