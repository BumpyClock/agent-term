import { useCallback, useEffect, useMemo, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { Session } from '../../store/terminalStore';
import { getToolTitle } from './utils';
import type { McpScope } from './types';

type McpInfo = {
  name: string;
  description: string;
  command: string;
  url: string;
  transport: string;
};

type McpItem = McpInfo & { isOrphan: boolean };

type McpManagerDialogProps = {
  session: Session;
  onClose: () => void;
};

const scopeLabels: Record<McpScope, string> = {
  global: 'Shared',
  local: 'Project',
};

function resolveTransportLabel(info: McpInfo): string {
  if (info.transport) return info.transport.toUpperCase();
  if (info.url) return 'HTTP';
  return 'STDIO';
}

function normalizeMcpInfo(name: string, info?: McpInfo): McpItem {
  if (info) {
    return { ...info, isOrphan: false };
  }
  return {
    name,
    description: 'Not in config.toml',
    command: '',
    url: '',
    transport: '',
    isOrphan: true,
  };
}

export function McpManagerDialog({ session, onClose }: McpManagerDialogProps) {
  const [scope, setScope] = useState<McpScope>('global');
  const [available, setAvailable] = useState<McpInfo[]>([]);
  const [attachedGlobal, setAttachedGlobal] = useState<string[]>([]);
  const [attachedLocal, setAttachedLocal] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [isWorking, setIsWorking] = useState(false);
  const [error, setError] = useState('');

  const projectPath = session.projectPath?.trim() || '';
  const hasProjectPath = projectPath.length > 0;

  const loadData = useCallback(async () => {
    setIsLoading(true);
    setError('');
    try {
      const list = await invoke<McpInfo[]>('mcp_list');
      const globalNames = await invoke<string[]>('mcp_attached', {
        scope: 'global',
        projectPath: null,
      });
      const localNames = hasProjectPath
        ? await invoke<string[]>('mcp_attached', {
            scope: 'local',
            projectPath,
          })
        : [];
      setAvailable(list);
      setAttachedGlobal(globalNames);
      setAttachedLocal(localNames);
    } catch (err) {
      console.error('Failed to load MCPs:', err);
      setError('Failed to load MCPs');
    } finally {
      setIsLoading(false);
    }
  }, [hasProjectPath, projectPath]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const availableMap = useMemo(() => {
    return new Map(available.map((item) => [item.name, item]));
  }, [available]);

  const attachedNames = scope === 'global' ? attachedGlobal : attachedLocal;
  const attachedItems = useMemo(() => {
    const names = new Set(attachedNames);
    return Array.from(names)
      .map((name) => normalizeMcpInfo(name, availableMap.get(name)))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [attachedNames, availableMap]);

  const availableItems = useMemo(() => {
    const attachedSet = new Set(attachedNames);
    return available
      .filter((item) => !attachedSet.has(item.name))
      .map((item) => normalizeMcpInfo(item.name, item))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [available, attachedNames]);

  const handleAttach = async (name: string) => {
    if (!hasProjectPath && scope === 'local') {
      setError('Project path is required for local MCPs');
      return;
    }
    setIsWorking(true);
    setError('');
    try {
      await invoke('mcp_attach', {
        scope,
        projectPath: scope === 'local' ? projectPath : null,
        mcpName: name,
      });
      await loadData();
    } catch (err) {
      console.error('Failed to attach MCP:', err);
      setError('Failed to attach MCP');
    } finally {
      setIsWorking(false);
    }
  };

  const handleDetach = async (name: string) => {
    if (!hasProjectPath && scope === 'local') {
      setError('Project path is required for local MCPs');
      return;
    }
    setIsWorking(true);
    setError('');
    try {
      await invoke('mcp_detach', {
        scope,
        projectPath: scope === 'local' ? projectPath : null,
        mcpName: name,
      });
      await loadData();
    } catch (err) {
      console.error('Failed to detach MCP:', err);
      setError('Failed to detach MCP');
    } finally {
      setIsWorking(false);
    }
  };

  const toolTitle = getToolTitle(session.tool);
  const scopeHint = scope === 'global'
    ? 'Shared across all agents'
    : 'Applies only to this project';

  return (
    <div className="dialog-overlay" onClick={onClose}>
      <div
        className="dialog mcp-dialog"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="dialog-title">MCP Manager</div>
        <div className="mcp-subtitle">{toolTitle} - {session.title}</div>
        <div className="mcp-scope-row">
          <button
            className={`mcp-scope-tab ${scope === 'global' ? 'active' : ''}`}
            onClick={() => setScope('global')}
            type="button"
          >
            {scopeLabels.global}
          </button>
          <button
            className={`mcp-scope-tab ${scope === 'local' ? 'active' : ''} ${!hasProjectPath ? 'disabled' : ''}`}
            onClick={() => {
              if (hasProjectPath) setScope('local');
            }}
            type="button"
          >
            {scopeLabels.local}
          </button>
          <div className="mcp-scope-hint">{scopeHint}</div>
        </div>
        {!hasProjectPath && (
          <div className="mcp-warning">
            Set a project path to manage local MCPs
          </div>
        )}
        <div className="mcp-columns">
          <div className="mcp-column">
            <div className="mcp-column-header">Attached</div>
            {isLoading ? (
              <div className="mcp-empty">Loading...</div>
            ) : attachedItems.length === 0 ? (
              <div className="mcp-empty">No MCPs attached</div>
            ) : (
              attachedItems.map((item) => (
                <div key={item.name} className="mcp-item">
                  <div className="mcp-item-main">
                    <div className="mcp-item-name">
                      {item.name}
                      {item.isOrphan && <span className="mcp-tag mcp-tag-warn">orphan</span>}
                    </div>
                    <div className="mcp-item-desc">
                      {item.description || 'No description'}
                    </div>
                  </div>
                  <div className="mcp-item-actions">
                    <span className="mcp-tag">{resolveTransportLabel(item)}</span>
                    <button
                      className="mcp-action"
                      onClick={() => handleDetach(item.name)}
                      disabled={isWorking}
                      type="button"
                    >
                      Detach
                    </button>
                  </div>
                </div>
              ))
            )}
          </div>
          <div className="mcp-column">
            <div className="mcp-column-header">Available</div>
            {isLoading ? (
              <div className="mcp-empty">Loading...</div>
            ) : availableItems.length === 0 ? (
              <div className="mcp-empty">No MCPs available</div>
            ) : (
              availableItems.map((item) => (
                <div key={item.name} className="mcp-item">
                  <div className="mcp-item-main">
                    <div className="mcp-item-name">{item.name}</div>
                    <div className="mcp-item-desc">
                      {item.description || 'No description'}
                    </div>
                  </div>
                  <div className="mcp-item-actions">
                    <span className="mcp-tag">{resolveTransportLabel(item)}</span>
                    <button
                      className="mcp-action mcp-action-primary"
                      onClick={() => handleAttach(item.name)}
                      disabled={isWorking}
                      type="button"
                    >
                      Attach
                    </button>
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
        {error && <div className="mcp-error">{error}</div>}
        <div className="dialog-actions">
          <button className="dialog-secondary" onClick={onClose} type="button">
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
