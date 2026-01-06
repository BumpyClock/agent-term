import { useCallback, useEffect, useMemo, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { MCPDef } from './settingsTypes';
import type { MCPPoolSettings } from './settingsTypes';

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
      <div className="dialog settings-dialog" onClick={(event) => event.stopPropagation()}>
        <div className="dialog-title">Settings</div>
        {isLoading ? (
          <div className="settings-loading">Loading MCP settings...</div>
        ) : (
          <>
            <section className="settings-section">
              <header className="settings-section-header">
                <div>
                  <div className="settings-section-title">MCP servers</div>
                  <div className="settings-section-subtitle">
                    Configure MCP definitions shared across all projects
                  </div>
                </div>
                <button className="settings-add-btn" type="button" onClick={addMcp}>
                  + Add MCP
                </button>
              </header>
              <div className="settings-mcp-list">
                {mcps.length === 0 && (
                  <div className="settings-empty">No MCPs configured yet</div>
                )}
                {mcps.map((item, index) => (
                  <div key={item.id} className="settings-mcp-card">
                    <div className="settings-mcp-row">
                      <label>
                        Name
                        <input
                          value={item.name}
                          onChange={(event) =>
                            updateMcp(index, { name: event.target.value })
                          }
                          placeholder="exa"
                        />
                      </label>
                      <label>
                        Description
                        <input
                          value={item.description}
                          onChange={(event) =>
                            updateMcp(index, { description: event.target.value })
                          }
                          placeholder="Web search via Exa"
                        />
                      </label>
                    </div>
                    <div className="settings-mcp-row">
                      <label>
                        Command
                        <input
                          value={item.command}
                          onChange={(event) =>
                            updateMcp(index, { command: event.target.value })
                          }
                          placeholder="npx"
                        />
                      </label>
                      <label>
                        Args
                        <input
                          value={joinList(item.args || [])}
                          onChange={(event) =>
                            updateMcp(index, { args: parseList(event.target.value) })
                          }
                          placeholder="-y, exa-mcp-server"
                        />
                      </label>
                    </div>
                    <div className="settings-mcp-row">
                      <label>
                        URL
                        <input
                          value={item.url}
                          onChange={(event) =>
                            updateMcp(index, { url: event.target.value })
                          }
                          placeholder="http://localhost:8000/mcp"
                        />
                      </label>
                      <label>
                        Transport
                        <select
                          value={item.transport}
                          onChange={(event) =>
                            updateMcp(index, { transport: event.target.value })
                          }
                        >
                          <option value="">Auto</option>
                          <option value="stdio">stdio</option>
                          <option value="http">http</option>
                          <option value="sse">sse</option>
                        </select>
                      </label>
                    </div>
                    <div className="settings-mcp-row">
                      <label className="settings-mcp-env">
                        Env (KEY=VALUE per line)
                        <textarea
                          value={envText[item.id] || ''}
                          onChange={(event) => handleEnvChange(item.id, event.target.value)}
                          placeholder="EXA_API_KEY=..."
                        />
                      </label>
                      <div className="settings-mcp-actions">
                        <button
                          className="settings-remove-btn"
                          type="button"
                          onClick={() => removeMcp(index)}
                        >
                          Remove
                        </button>
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </section>

            <section className="settings-section">
              <header className="settings-section-header">
                <div>
                  <div className="settings-section-title">MCP socket pool</div>
                  <div className="settings-section-subtitle">
                    Share MCP processes across agents to save memory
                  </div>
                </div>
              </header>
              <div className="settings-pool-grid">
                <label className="settings-checkbox">
                  <input
                    type="checkbox"
                    checked={pool.enabled}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, enabled: event.target.checked }))
                    }
                  />
                  Enable socket pool
                </label>
                <label className="settings-checkbox">
                  <input
                    type="checkbox"
                    checked={pool.autoStart}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, autoStart: event.target.checked }))
                    }
                  />
                  Auto-start pool
                </label>
                <label className="settings-checkbox">
                  <input
                    type="checkbox"
                    checked={pool.startOnDemand}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, startOnDemand: event.target.checked }))
                    }
                  />
                  Start on demand
                </label>
                <label className="settings-checkbox">
                  <input
                    type="checkbox"
                    checked={pool.shutdownOnExit}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, shutdownOnExit: event.target.checked }))
                    }
                  />
                  Shutdown on exit
                </label>
                <label className="settings-checkbox">
                  <input
                    type="checkbox"
                    checked={pool.poolAll}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, poolAll: event.target.checked }))
                    }
                  />
                  Pool all MCPs
                </label>
                <label className="settings-checkbox">
                  <input
                    type="checkbox"
                    checked={pool.fallbackToStdio}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, fallbackToStdio: event.target.checked }))
                    }
                  />
                  Fallback to stdio
                </label>
              </div>
              <div className="settings-pool-row">
                <label>
                  Pool MCPs (comma or newline separated)
                  <input
                    value={poolMcpsText}
                    onChange={(event) =>
                      setPool((prev) => ({
                        ...prev,
                        poolMcps: parseList(event.target.value),
                      }))
                    }
                    placeholder="exa, memory, firecrawl"
                  />
                </label>
                <label>
                  Exclude MCPs
                  <input
                    value={excludeMcpsText}
                    onChange={(event) =>
                      setPool((prev) => ({
                        ...prev,
                        excludeMcps: parseList(event.target.value),
                      }))
                    }
                    placeholder="chrome-devtools"
                  />
                </label>
              </div>
              <div className="settings-pool-row">
                <label>
                  Port start
                  <input
                    type="number"
                    value={pool.portStart}
                    onChange={(event) =>
                      setPool((prev) => ({
                        ...prev,
                        portStart: Number(event.target.value || 0),
                      }))
                    }
                  />
                </label>
                <label>
                  Port end
                  <input
                    type="number"
                    value={pool.portEnd}
                    onChange={(event) =>
                      setPool((prev) => ({
                        ...prev,
                        portEnd: Number(event.target.value || 0),
                      }))
                    }
                  />
                </label>
                <label className="settings-checkbox settings-checkbox-inline">
                  <input
                    type="checkbox"
                    checked={pool.showPoolStatus}
                    onChange={(event) =>
                      setPool((prev) => ({ ...prev, showPoolStatus: event.target.checked }))
                    }
                  />
                  Show pool status
                </label>
              </div>
            </section>

            {error && <div className="settings-error">{error}</div>}
            <div className="dialog-actions">
              <button className="dialog-secondary" type="button" onClick={onClose}>
                Cancel
              </button>
              <button
                className="dialog-primary"
                type="button"
                onClick={handleSave}
                disabled={isSaving}
              >
                Save settings
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
