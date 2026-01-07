import { useState, useEffect, useCallback, useRef } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { PoolStatusResponse, McpServerStatus } from '@/components/sidebar/settingsTypes';

type UsePoolStatusOptions = {
  enabled?: boolean;
  pollInterval?: number;
};

export function usePoolStatus(options: UsePoolStatusOptions = {}) {
  const { enabled = true, pollInterval = 5000 } = options;
  const [status, setStatus] = useState<PoolStatusResponse | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const intervalRef = useRef<number | null>(null);

  const fetchStatus = useCallback(async () => {
    try {
      const result = await invoke<PoolStatusResponse>('mcp_pool_status');
      setStatus(result);
      setError(null);
    } catch (err) {
      console.error('Failed to fetch pool status:', err);
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsLoading(false);
    }
  }, []);

  const restartServer = useCallback(async (name: string) => {
    try {
      await invoke<boolean>('mcp_restart_server', { name });
      await fetchStatus();
      return true;
    } catch (err) {
      console.error(`Failed to restart server ${name}:`, err);
      return false;
    }
  }, [fetchStatus]);

  const stopServer = useCallback(async (name: string) => {
    try {
      await invoke<boolean>('mcp_stop_server', { name });
      await fetchStatus();
      return true;
    } catch (err) {
      console.error(`Failed to stop server ${name}:`, err);
      return false;
    }
  }, [fetchStatus]);

  const startServer = useCallback(async (name: string) => {
    try {
      await invoke<boolean>('mcp_start_server', { name });
      await fetchStatus();
      return true;
    } catch (err) {
      console.error(`Failed to start server ${name}:`, err);
      return false;
    }
  }, [fetchStatus]);

  useEffect(() => {
    if (!enabled) {
      setStatus(null);
      setIsLoading(false);
      return;
    }

    fetchStatus();

    if (pollInterval > 0) {
      intervalRef.current = window.setInterval(fetchStatus, pollInterval);
    }

    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
      }
    };
  }, [enabled, pollInterval, fetchStatus]);

  const getServerStatus = useCallback((name: string): McpServerStatus | undefined => {
    return status?.servers.find((s) => s.name === name);
  }, [status]);

  return {
    status,
    isLoading,
    error,
    refetch: fetchStatus,
    restartServer,
    stopServer,
    startServer,
    getServerStatus,
  };
}
