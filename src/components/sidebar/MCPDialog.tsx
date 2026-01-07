// ABOUTME: Dedicated dialog for MCP server configuration
// ABOUTME: Manages MCP servers and pool settings independent of the main settings dialog

import { useCallback, useEffect, useMemo, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { MCPDef, MCPPoolSettings } from './settingsTypes';
import { Button } from '@/components/ui/button';
import { MCPSettings } from '@/components/settings';
import type { McpItem } from '@/components/settings';

type MCPDialogProps = {
  onClose: () => void;
};

type McpSettingsPayload = {
  mcps: Record<string, MCPDef>;
  mcpPool: MCPPoolSettings;
};

const makeId = () => {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }
  return `mcp-${Date.now()}-${Math.random().toString(16).slice(2)}`;
};

const emptyMcp = (): McpItem => ({
  id: makeId(),
  name: '',
  command: '',
  args: [],
  env: {},
  description: '',
  url: '',
  transport: '',
});

const emptyPool: MCPPoolSettings = {
  enabled: false,
  autoStart: true,
  portStart: 8001,
  portEnd: 8050,
  startOnDemand: false,
  shutdownOnExit: true,
  poolMcps: [],
  fallbackToStdio: true,
  showPoolStatus: true,
  poolAll: false,
  excludeMcps: [],
};

const envToText = (env: Record<string, string>) =>
  Object.entries(env)
    .map(([key, val]) => `${key}=${val}`)
    .join('\n');

const textToEnv = (value: string) => {
  const result: Record<string, string> = {};
  value
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .forEach((line) => {
      const idx = line.indexOf('=');
      if (idx > 0) {
        const key = line.slice(0, idx).trim();
        const val = line.slice(idx + 1).trim();
        if (key) {
          result[key] = val;
        }
      }
    });
  return result;
};

export function MCPDialog({ onClose }: MCPDialogProps) {
  const [mcps, setMcps] = useState<McpItem[]>([]);
  const [pool, setPool] = useState<MCPPoolSettings>(emptyPool);
  const [envText, setEnvText] = useState<Record<string, string>>({});
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState('');

  const loadSettings = useCallback(async () => {
    setIsLoading(true);
    setError('');
    try {
      const payload = await invoke<McpSettingsPayload>('mcp_get_settings');
      const mcpItems = Object.entries(payload.mcps).map(([name, def]) => ({
        id: makeId(),
        name,
        ...def,
      }));
      setMcps(mcpItems);
      setPool(payload.mcpPool);
      const envMap: Record<string, string> = {};
      mcpItems.forEach((item) => {
        envMap[item.id] = envToText(item.env || {});
      });
      setEnvText(envMap);
    } catch (err) {
      console.error('Failed to load MCP settings:', err);
      setError('Failed to load MCP settings');
      setMcps([]);
      setPool(emptyPool);
      setEnvText({});
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  const addMcp = () => {
    setMcps((prev) => [...prev, emptyMcp()]);
  };

  const removeMcp = (index: number) => {
    setMcps((prev) => {
      const next = [...prev];
      const [removed] = next.splice(index, 1);
      if (removed) {
        setEnvText((env) => {
          const updated = { ...env };
          delete updated[removed.id];
          return updated;
        });
      }
      return next;
    });
  };

  const handleEnvChange = (id: string, value: string) => {
    setEnvText((prev) => ({ ...prev, [id]: value }));
  };

  const handlePoolChange = (updates: Partial<MCPPoolSettings>) => {
    setPool((prev) => ({ ...prev, ...updates }));
  };

  const validationError = useMemo(() => {
    const names = mcps.map((item) => item.name.trim()).filter(Boolean);
    const duplicates = names.filter((name, idx) => names.indexOf(name) !== idx);
    if (duplicates.length > 0) {
      return 'MCP names must be unique';
    }
    if (mcps.some((item) => !item.name.trim())) {
      return 'MCP name is required';
    }
    return '';
  }, [mcps]);

  const handleSave = async () => {
    if (validationError) {
      setError(validationError);
      return;
    }
    setIsSaving(true);
    setError('');
    try {
      const map: Record<string, MCPDef> = {};
      mcps.forEach((item) => {
        const name = item.name.trim();
        map[name] = {
          command: item.command.trim(),
          args: item.args,
          env: textToEnv(envText[item.id] || ''),
          description: item.description.trim(),
          url: item.url.trim(),
          transport: item.transport.trim(),
        };
      });
      await invoke('mcp_set_settings', {
        settings: {
          mcps: map,
          mcpPool: pool,
        },
      });
      onClose();
    } catch (err) {
      console.error('Failed to save MCP settings:', err);
      setError('Failed to save MCP settings');
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <div className="dialog-overlay" onClick={onClose}>
      <div className="settings-dialog" onClick={(event) => event.stopPropagation()}>
        <div className="flex-shrink-0 pb-4">
          <h2 className="text-xl font-semibold">MCP Servers</h2>
        </div>

        <div className="flex-1 flex flex-col min-h-0 overflow-y-auto">
          {isLoading ? (
            <div className="text-muted-foreground">Loading MCP settings...</div>
          ) : (
            <MCPSettings
              mcps={mcps}
              pool={pool}
              envText={envText}
              onMcpsChange={setMcps}
              onPoolChange={handlePoolChange}
              onEnvTextChange={handleEnvChange}
              onAddMcp={addMcp}
              onRemoveMcp={removeMcp}
            />
          )}
        </div>

        {error && <div className="text-destructive text-sm flex-shrink-0 pt-2">{error}</div>}

        <div className="flex justify-end gap-3 pt-4 flex-shrink-0">
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={isSaving}>
            Save settings
          </Button>
        </div>
      </div>
    </div>
  );
}
