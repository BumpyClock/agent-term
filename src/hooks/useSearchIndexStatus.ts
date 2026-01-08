import { useCallback, useEffect, useRef, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';

type SearchIndexStatusResponse = {
  indexed: boolean;
  messageCount: number;
  fileCount: number;
  lastIndexedAt: string | null;
};

type SearchIndexStatus = {
  indexed: boolean;
  messageCount: number;
  fileCount: number;
  lastIndexedAt?: string;
};

type UseSearchIndexStatusOptions = {
  enabled?: boolean;
  deferMs?: number;
  reindexAfterHours?: number;
};

type IdleControl = Window & {
  requestIdleCallback?: (callback: IdleRequestCallback, options?: IdleRequestOptions) => number;
  cancelIdleCallback?: (handle: number) => void;
};

const DEFAULT_DEFER_MS = 2000;
const DEFAULT_REINDEX_HOURS = 24;

export function useSearchIndexStatus(options: UseSearchIndexStatusOptions = {}) {
  const {
    enabled = true,
    deferMs = DEFAULT_DEFER_MS,
    reindexAfterHours = DEFAULT_REINDEX_HOURS,
  } = options;
  const [status, setStatus] = useState<SearchIndexStatus | undefined>(undefined);
  const [isIndexing, setIsIndexing] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const scheduledRef = useRef(false);

  const normalizeStatus = useCallback((result: SearchIndexStatusResponse): SearchIndexStatus => ({
    indexed: result.indexed,
    messageCount: result.messageCount,
    fileCount: result.fileCount,
    lastIndexedAt: result.lastIndexedAt ?? undefined,
  }), []);

  const fetchStatus = useCallback(async () => {
    const result = await invoke<SearchIndexStatusResponse>('search_index_status');
    const normalized = normalizeStatus(result);
    setStatus(normalized);
    setError(undefined);
    return normalized;
  }, [normalizeStatus]);

  const triggerReindex = useCallback(async () => {
    setIsIndexing(true);
    try {
      const result = await invoke<SearchIndexStatusResponse>('search_reindex');
      setStatus(normalizeStatus(result));
      setError(undefined);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsIndexing(false);
    }
  }, [normalizeStatus]);

  const shouldReindex = useCallback((current?: SearchIndexStatus) => {
    if (!current) return true;
    if (!current.indexed) return true;
    if (!current.lastIndexedAt) return true;
    const parsed = Date.parse(current.lastIndexedAt);
    if (Number.isNaN(parsed)) return true;
    const ageMs = Date.now() - parsed;
    return ageMs >= reindexAfterHours * 60 * 60 * 1000;
  }, [reindexAfterHours]);

  useEffect(() => {
    if (!enabled) {
      setStatus(undefined);
      setIsIndexing(false);
      setError(undefined);
      scheduledRef.current = false;
      return;
    }

    let cancelled = false;
    let timeoutId: number | undefined;
    let idleId: number | undefined;

    fetchStatus()
      .then((current) => {
        if (cancelled || scheduledRef.current) return;
        if (!shouldReindex(current)) return;
        scheduledRef.current = true;
        const runner = () => {
          if (!cancelled) {
            triggerReindex();
          }
        };
        const idleControl = window as IdleControl;
        if (idleControl.requestIdleCallback) {
          idleId = idleControl.requestIdleCallback(runner, { timeout: deferMs });
        } else {
          timeoutId = window.setTimeout(runner, deferMs);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : String(err));
        }
      });

    return () => {
      cancelled = true;
      if (timeoutId !== undefined) {
        clearTimeout(timeoutId);
      }
      const idleControl = window as IdleControl;
      if (idleId !== undefined && idleControl.cancelIdleCallback) {
        idleControl.cancelIdleCallback(idleId);
      }
    };
  }, [enabled, deferMs, fetchStatus, shouldReindex, triggerReindex]);

  return {
    status,
    isIndexing,
    error,
    refetch: fetchStatus,
    reindex: triggerReindex,
  };
}
