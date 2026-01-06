import { useCallback, useEffect, useMemo, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { MCPDef } from './settingsTypes';
import type { MCPPoolSettings } from './settingsTypes';
import { useTheme } from '../theme-provider';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Textarea } from '@/components/ui/textarea';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';

type SettingsDialogProps = {
  onClose: () => void;
};

type McpItem = MCPDef & { id: string; name: string };

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

const joinList = (items: string[]) => items.join(', ');

const parseList = (value: string) =>
  value
    .split(/[,\n]/)
    .map((entry) => entry.trim())
    .filter(Boolean);

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

export function SettingsDialog({ onClose }: SettingsDialogProps) {
  const { theme, setTheme } = useTheme();
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

  const updateMcp = (index: number, updates: Partial<McpItem>) => {
    setMcps((prev) =>
      prev.map((item, i) => (i === index ? { ...item, ...updates } : item))
    );
  };

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

  const poolMcpsText = useMemo(() => joinList(pool.poolMcps || []), [pool.poolMcps]);
  const excludeMcpsText = useMemo(() => joinList(pool.excludeMcps || []), [pool.excludeMcps]);

  return (
    <div className="dialog-overlay" onClick={onClose}>
      <div className="settings-dialog" onClick={(event) => event.stopPropagation()}>
        <Card className="border-0 shadow-none bg-transparent">
          <CardHeader className="pb-4">
            <CardTitle className="text-xl">Settings</CardTitle>
          </CardHeader>
          <CardContent className="space-y-6">
            {isLoading ? (
              <div className="text-muted-foreground">Loading MCP settings...</div>
            ) : (
              <>
                {/* Appearance Section */}
                <Card>
                  <CardHeader className="pb-3">
                    <CardTitle className="text-base">Appearance</CardTitle>
                    <CardDescription>Choose how the app looks</CardDescription>
                  </CardHeader>
                  <CardContent>
                    <div className="flex gap-2">
                      <Button
                        variant={theme === 'system' ? 'default' : 'outline'}
                        className="flex-1"
                        onClick={() => setTheme('system')}
                      >
                        System
                      </Button>
                      <Button
                        variant={theme === 'light' ? 'default' : 'outline'}
                        className="flex-1"
                        onClick={() => setTheme('light')}
                      >
                        Light
                      </Button>
                      <Button
                        variant={theme === 'dark' ? 'default' : 'outline'}
                        className="flex-1"
                        onClick={() => setTheme('dark')}
                      >
                        Dark
                      </Button>
                    </div>
                  </CardContent>
                </Card>

                {/* MCP Servers Section */}
                <Card>
                  <CardHeader className="pb-3">
                    <div className="flex items-center justify-between">
                      <div>
                        <CardTitle className="text-base">MCP servers</CardTitle>
                        <CardDescription>Configure MCP definitions shared across all projects</CardDescription>
                      </div>
                      <Button size="sm" onClick={addMcp}>
                        + Add MCP
                      </Button>
                    </div>
                  </CardHeader>
                  <CardContent className="space-y-4">
                    {mcps.length === 0 && (
                      <div className="text-muted-foreground text-sm">No MCPs configured yet</div>
                    )}
                    {mcps.map((item, index) => (
                      <Card key={item.id} className="bg-muted/50">
                        <CardContent className="pt-4 space-y-4">
                          <div className="grid grid-cols-2 gap-4">
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-name-${item.id}`}>Name</Label>
                              <Input
                                id={`mcp-name-${item.id}`}
                                value={item.name}
                                onChange={(e) => updateMcp(index, { name: e.target.value })}
                                placeholder="exa"
                              />
                            </div>
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-desc-${item.id}`}>Description</Label>
                              <Input
                                id={`mcp-desc-${item.id}`}
                                value={item.description}
                                onChange={(e) => updateMcp(index, { description: e.target.value })}
                                placeholder="Web search via Exa"
                              />
                            </div>
                          </div>
                          <div className="grid grid-cols-2 gap-4">
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-cmd-${item.id}`}>Command</Label>
                              <Input
                                id={`mcp-cmd-${item.id}`}
                                value={item.command}
                                onChange={(e) => updateMcp(index, { command: e.target.value })}
                                placeholder="npx"
                              />
                            </div>
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-args-${item.id}`}>Args</Label>
                              <Input
                                id={`mcp-args-${item.id}`}
                                value={joinList(item.args || [])}
                                onChange={(e) => updateMcp(index, { args: parseList(e.target.value) })}
                                placeholder="-y, exa-mcp-server"
                              />
                            </div>
                          </div>
                          <div className="grid grid-cols-2 gap-4">
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-url-${item.id}`}>URL</Label>
                              <Input
                                id={`mcp-url-${item.id}`}
                                value={item.url}
                                onChange={(e) => updateMcp(index, { url: e.target.value })}
                                placeholder="http://localhost:8000/mcp"
                              />
                            </div>
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-transport-${item.id}`}>Transport</Label>
                              <NativeSelect
                                id={`mcp-transport-${item.id}`}
                                value={item.transport}
                                onChange={(e) => updateMcp(index, { transport: e.target.value })}
                              >
                                <NativeSelectOption value="">Auto</NativeSelectOption>
                                <NativeSelectOption value="stdio">stdio</NativeSelectOption>
                                <NativeSelectOption value="http">http</NativeSelectOption>
                                <NativeSelectOption value="sse">sse</NativeSelectOption>
                              </NativeSelect>
                            </div>
                          </div>
                          <div className="grid grid-cols-2 gap-4">
                            <div className="space-y-2">
                              <Label htmlFor={`mcp-env-${item.id}`}>Env (KEY=VALUE per line)</Label>
                              <Textarea
                                id={`mcp-env-${item.id}`}
                                value={envText[item.id] || ''}
                                onChange={(e) => handleEnvChange(item.id, e.target.value)}
                                placeholder="EXA_API_KEY=..."
                                className="min-h-[80px]"
                              />
                            </div>
                            <div className="flex items-end justify-end">
                              <Button
                                variant="destructive"
                                size="sm"
                                onClick={() => removeMcp(index)}
                              >
                                Remove
                              </Button>
                            </div>
                          </div>
                        </CardContent>
                      </Card>
                    ))}
                  </CardContent>
                </Card>

                {/* MCP Socket Pool Section */}
                <Card>
                  <CardHeader className="pb-3">
                    <CardTitle className="text-base">MCP socket pool</CardTitle>
                    <CardDescription>Share MCP processes across agents to save memory</CardDescription>
                  </CardHeader>
                  <CardContent className="space-y-4">
                    <div className="grid grid-cols-2 gap-4 sm:grid-cols-3">
                      <div className="flex items-center space-x-2">
                        <Checkbox
                          id="pool-enabled"
                          checked={pool.enabled}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, enabled: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-enabled" className="text-sm font-normal">
                          Enable socket pool
                        </Label>
                      </div>
                      <div className="flex items-center space-x-2">
                        <Checkbox
                          id="pool-autostart"
                          checked={pool.autoStart}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, autoStart: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-autostart" className="text-sm font-normal">
                          Auto-start pool
                        </Label>
                      </div>
                      <div className="flex items-center space-x-2">
                        <Checkbox
                          id="pool-ondemand"
                          checked={pool.startOnDemand}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, startOnDemand: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-ondemand" className="text-sm font-normal">
                          Start on demand
                        </Label>
                      </div>
                      <div className="flex items-center space-x-2">
                        <Checkbox
                          id="pool-shutdown"
                          checked={pool.shutdownOnExit}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, shutdownOnExit: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-shutdown" className="text-sm font-normal">
                          Shutdown on exit
                        </Label>
                      </div>
                      <div className="flex items-center space-x-2">
                        <Checkbox
                          id="pool-all"
                          checked={pool.poolAll}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, poolAll: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-all" className="text-sm font-normal">
                          Pool all MCPs
                        </Label>
                      </div>
                      <div className="flex items-center space-x-2">
                        <Checkbox
                          id="pool-fallback"
                          checked={pool.fallbackToStdio}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, fallbackToStdio: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-fallback" className="text-sm font-normal">
                          Fallback to stdio
                        </Label>
                      </div>
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                      <div className="space-y-2">
                        <Label htmlFor="pool-mcps">Pool MCPs (comma or newline separated)</Label>
                        <Input
                          id="pool-mcps"
                          value={poolMcpsText}
                          onChange={(e) =>
                            setPool((prev) => ({
                              ...prev,
                              poolMcps: parseList(e.target.value),
                            }))
                          }
                          placeholder="exa, memory, firecrawl"
                        />
                      </div>
                      <div className="space-y-2">
                        <Label htmlFor="exclude-mcps">Exclude MCPs</Label>
                        <Input
                          id="exclude-mcps"
                          value={excludeMcpsText}
                          onChange={(e) =>
                            setPool((prev) => ({
                              ...prev,
                              excludeMcps: parseList(e.target.value),
                            }))
                          }
                          placeholder="chrome-devtools"
                        />
                      </div>
                    </div>
                    <div className="grid grid-cols-3 gap-4 items-end">
                      <div className="space-y-2">
                        <Label htmlFor="port-start">Port start</Label>
                        <Input
                          id="port-start"
                          type="number"
                          value={pool.portStart}
                          onChange={(e) =>
                            setPool((prev) => ({
                              ...prev,
                              portStart: Number(e.target.value || 0),
                            }))
                          }
                        />
                      </div>
                      <div className="space-y-2">
                        <Label htmlFor="port-end">Port end</Label>
                        <Input
                          id="port-end"
                          type="number"
                          value={pool.portEnd}
                          onChange={(e) =>
                            setPool((prev) => ({
                              ...prev,
                              portEnd: Number(e.target.value || 0),
                            }))
                          }
                        />
                      </div>
                      <div className="flex items-center space-x-2 pb-2">
                        <Checkbox
                          id="pool-status"
                          checked={pool.showPoolStatus}
                          onCheckedChange={(checked) =>
                            setPool((prev) => ({ ...prev, showPoolStatus: checked === true }))
                          }
                        />
                        <Label htmlFor="pool-status" className="text-sm font-normal">
                          Show pool status
                        </Label>
                      </div>
                    </div>
                  </CardContent>
                </Card>

                {error && (
                  <div className="text-destructive text-sm">{error}</div>
                )}

                <div className="flex justify-end gap-3 pt-2">
                  <Button variant="outline" onClick={onClose}>
                    Cancel
                  </Button>
                  <Button onClick={handleSave} disabled={isSaving}>
                    Save settings
                  </Button>
                </div>
              </>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
