import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { MCPServerListItem } from './MCPServerListItem';
import { MCPServerForm } from './MCPServerForm';
import { PoolSettings } from './PoolSettings';
import { PoolDiagnostics } from './PoolDiagnostics';
import { usePoolStatus } from '@/hooks/usePoolStatus';
import type { McpItem, MCPPoolSettings } from './types';

type MCPSettingsProps = {
  mcps: McpItem[];
  pool: MCPPoolSettings;
  envText: Record<string, string>;
  onMcpsChange: (mcps: McpItem[]) => void;
  onPoolChange: (updates: Partial<MCPPoolSettings>) => void;
  onEnvTextChange: (id: string, value: string) => void;
  onViewChange?: (view: 'list' | 'form') => void;
};

const makeId = () => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `mcp-${Date.now()}-${Math.random().toString(16).slice(2)}`;
};

const joinList = (items: string[]) => items.join(', ');
const parseList = (value: string) =>
  value.split(/[,\n]/).map((s) => s.trim()).filter(Boolean);

const textToEnv = (value: string) => {
  const result: Record<string, string> = {};
  value.split('\n').map((line) => line.trim()).filter(Boolean).forEach((line) => {
    const idx = line.indexOf('=');
    if (idx > 0) {
      const key = line.slice(0, idx).trim();
      const val = line.slice(idx + 1).trim();
      if (key) result[key] = val;
    }
  });
  return result;
};

type View = 'list' | 'form';

export function MCPSettings({
  mcps,
  pool,
  envText,
  onMcpsChange,
  onPoolChange,
  onEnvTextChange,
  onViewChange,
}: MCPSettingsProps) {
  const [view, setView] = useState<View>('list');
  const [dialogMode, setDialogMode] = useState<'add' | 'edit'>('add');
  const [editingIndex, setEditingIndex] = useState<number | null>(null);
  const [formName, setFormName] = useState('');
  const [formCommand, setFormCommand] = useState('');
  const [formArgs, setFormArgs] = useState('');
  const [formDescription, setFormDescription] = useState('');
  const [formUrl, setFormUrl] = useState('');
  const [formTransport, setFormTransport] = useState('');
  const [formEnvText, setFormEnvText] = useState('');
  const [formIsPooled, setFormIsPooled] = useState(false);
  const [validationError, setValidationError] = useState('');

  const {
    status: poolStatus,
    isLoading: poolLoading,
    error: poolError,
    refetch: refetchPool,
    restartServer,
    stopServer,
    startServer,
    getServerStatus,
  } = usePoolStatus({
    enabled: pool.enabled && pool.showPoolStatus,
    pollInterval: 5000,
  });

  const handleAddClick = () => {
    setFormName('');
    setFormCommand('');
    setFormArgs('');
    setFormDescription('');
    setFormUrl('');
    setFormTransport('');
    setFormEnvText('');
    setFormIsPooled(false);
    setDialogMode('add');
    setEditingIndex(null);
    setValidationError('');
    setView('form');
    onViewChange?.('form');
  };

  const handleEditClick = (index: number) => {
    const item = mcps[index];
    setFormName(item.name);
    setFormCommand(item.command);
    setFormArgs(joinList(item.args || []));
    setFormDescription(item.description);
    setFormUrl(item.url);
    setFormTransport(item.transport);
    setFormEnvText(envText[item.id] || '');
    setFormIsPooled(pool.poolMcps.includes(item.name));
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
      setValidationError('MCP name is required');
      return;
    }
    const existingNames = mcps
      .filter((_, i) => i !== editingIndex)
      .map((m) => m.name.trim());
    if (existingNames.includes(trimmedName)) {
      setValidationError('MCP names must be unique');
      return;
    }

    let updatedPoolMcps = [...pool.poolMcps];

    if (dialogMode === 'add') {
      const newId = makeId();
      const newMcp: McpItem = {
        id: newId,
        name: trimmedName,
        enabled: true,
        command: formCommand.trim(),
        args: parseList(formArgs),
        description: formDescription.trim(),
        url: formUrl.trim(),
        transport: formTransport.trim(),
        env: textToEnv(formEnvText),
      };
      onMcpsChange([...mcps, newMcp]);
      onEnvTextChange(newId, formEnvText);
      // Add to poolMcps if pooled
      if (formIsPooled && !updatedPoolMcps.includes(trimmedName)) {
        updatedPoolMcps.push(trimmedName);
        onPoolChange({ poolMcps: updatedPoolMcps });
      }
    } else if (editingIndex !== null) {
      const item = mcps[editingIndex];
      const oldName = item.name;
      // Handle name change in poolMcps
      if (oldName !== trimmedName) {
        updatedPoolMcps = updatedPoolMcps.filter(name => name !== oldName);
        if (formIsPooled && !updatedPoolMcps.includes(trimmedName)) {
          updatedPoolMcps.push(trimmedName);
        }
      } else {
        // Same name, just update pooled state
        if (formIsPooled && !updatedPoolMcps.includes(trimmedName)) {
          updatedPoolMcps.push(trimmedName);
        } else if (!formIsPooled) {
          updatedPoolMcps = updatedPoolMcps.filter(name => name !== trimmedName);
        }
      }
      const updated = mcps.map((m, i) =>
        i === editingIndex
          ? {
              ...m,
              name: trimmedName,
              command: formCommand.trim(),
              args: parseList(formArgs),
              description: formDescription.trim(),
              url: formUrl.trim(),
              transport: formTransport.trim(),
              env: textToEnv(formEnvText),
            }
          : m
      );
      onMcpsChange(updated);
      onEnvTextChange(item.id, formEnvText);
      onPoolChange({ poolMcps: updatedPoolMcps });
    }
    setView('list');
    onViewChange?.('list');
  };

  const handleFormDelete = () => {
    if (editingIndex !== null) {
      const removed = mcps[editingIndex];
      onMcpsChange(mcps.filter((_, i) => i !== editingIndex));
      if (removed) {
        onEnvTextChange(removed.id, '');
        // Remove from poolMcps
        const updatedPoolMcps = pool.poolMcps.filter(name => name !== removed.name);
        onPoolChange({ poolMcps: updatedPoolMcps });
      }
    }
    setView('list');
    onViewChange?.('list');
  };

  const handleToggle = (index: number, enabled: boolean) => {
    const updated = mcps.map((m, i) => (i === index ? { ...m, enabled } : m));
    onMcpsChange(updated);
  };

  return (
    <div className="relative">
      {/* List View - only rendered when view === 'list' */}
      {view === 'list' && (
        <div className="space-y-6">
          <Card>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base">MCP servers</CardTitle>
                  <CardDescription>Configure MCP definitions shared across all projects</CardDescription>
                </div>
                <Button size="sm" onClick={handleAddClick}>
                  + Add MCP
                </Button>
              </div>
            </CardHeader>
            <CardContent className="space-y-2">
              {mcps.length === 0 && (
                <div className="text-muted-foreground text-sm">No MCPs configured yet</div>
              )}
              {mcps.map((item, index) => (
                <MCPServerListItem
                  key={item.id}
                  name={item.name}
                  description={item.description}
                  command={item.command}
                  url={item.url}
                  enabled={item.enabled ?? true}
                  poolStatus={pool.enabled ? getServerStatus(item.name)?.status : undefined}
                  onToggle={(enabled) => handleToggle(index, enabled)}
                  onClick={() => handleEditClick(index)}
                />
              ))}
            </CardContent>
          </Card>

          <PoolSettings pool={pool} onPoolChange={onPoolChange} />

          {pool.enabled && pool.showPoolStatus && (
            <PoolDiagnostics
              status={poolStatus}
              isLoading={poolLoading}
              error={poolError}
              onRefresh={refetchPool}
              onRestartServer={restartServer}
              onStopServer={stopServer}
              onStartServer={startServer}
            />
          )}
        </div>
      )}

      {/* Form View - only rendered when view === 'form', with slide-in animation */}
      {view === 'form' && (
        <div className="animate-in slide-in-from-right duration-200">
          <MCPServerForm
            mode={dialogMode}
            name={formName}
            command={formCommand}
            args={formArgs}
            description={formDescription}
            url={formUrl}
            transport={formTransport}
            envText={formEnvText}
            onNameChange={setFormName}
            onCommandChange={setFormCommand}
            onArgsChange={setFormArgs}
            onDescriptionChange={setFormDescription}
            onUrlChange={setFormUrl}
            onTransportChange={setFormTransport}
            onEnvTextChange={setFormEnvText}
            isPooled={formIsPooled}
            isPoolAllEnabled={pool.poolAll}
            onIsPooledChange={setFormIsPooled}
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
