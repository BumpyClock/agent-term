import { useEffect, useMemo, useState } from 'react';
import { AlertCircle, Database, Loader2, Server } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { usePoolStatus } from '@/hooks/usePoolStatus';
import { useSearchIndexStatus } from '@/hooks/useSearchIndexStatus';
import { UpdateNotification } from './UpdateNotification';
import './Sidebar.css';

const DEFER_MS = 2000;
const POOL_POLL_MS = 5000;
const REINDEX_AFTER_HOURS = 24;

export function SidebarStatusBar() {
  const [poolEnabled, setPoolEnabled] = useState(false);

  useEffect(() => {
    const timer = window.setTimeout(() => setPoolEnabled(true), DEFER_MS);
    return () => clearTimeout(timer);
  }, []);

  const {
    status: poolStatus,
    isLoading: poolLoading,
    error: poolError,
    refetch: refreshPool,
  } = usePoolStatus({ enabled: poolEnabled, pollInterval: POOL_POLL_MS });

  const {
    status: searchStatus,
    isIndexing,
    error: searchError,
    reindex,
  } = useSearchIndexStatus({
    deferMs: DEFER_MS,
    reindexAfterHours: REINDEX_AFTER_HOURS,
  });

  const poolSummary = useMemo(() => {
    if (!poolEnabled) {
      return { state: 'idle', text: '', meta: '' };
    }
    if (poolError) {
      return { state: 'error', text: 'MCP pool error', meta: poolError };
    }
    if (!poolStatus) {
      return { state: 'loading', text: 'MCP pool starting', meta: '' };
    }
    if (!poolStatus.enabled) {
      return { state: 'disabled', text: 'MCP pool off', meta: '' };
    }

    const servers = poolStatus.servers ?? [];
    const runningCount = servers.filter((server) => server.status === 'Running').length;
    const startingCount = servers.filter((server) => server.status === 'Starting').length;
    const failedCount = servers.filter((server) => server.status === 'Failed').length;
    const totalCount = poolStatus.serverCount ?? servers.length;
    const meta = totalCount > 0 ? `${runningCount}/${totalCount} running` : '';

    if (failedCount > 0) {
      return { state: 'error', text: 'MCP pool error', meta: `${failedCount} failed` };
    }
    if (startingCount > 0 || poolLoading) {
      return { state: 'starting', text: 'MCP pool starting', meta };
    }
    return { state: 'ready', text: 'MCP pool ready', meta };
  }, [poolEnabled, poolError, poolLoading, poolStatus]);

  const searchSummary = useMemo(() => {
    if (searchError) {
      return { state: 'error', text: 'Search index error', meta: 'Retry' };
    }
    if (isIndexing) {
      return { state: 'indexing', text: 'Indexing search', meta: '' };
    }
    if (searchStatus?.indexed) {
      return { state: 'ready', text: '', meta: '' };
    }
    return { state: 'idle', text: '', meta: '' };
  }, [isIndexing, searchError, searchStatus]);

  const showSearchRow = searchSummary.state === 'indexing' || searchSummary.state === 'error';
  const showPoolRow = poolSummary.state === 'starting' || poolSummary.state === 'loading' || poolSummary.state === 'error';
  const searchClickable = showSearchRow && !isIndexing;
  const poolClickable = showPoolRow;

  const SearchIcon = searchSummary.state === 'error'
    ? AlertCircle
    : searchSummary.state === 'indexing'
      ? Loader2
      : Database;

  const PoolIcon = poolSummary.state === 'error'
    ? AlertCircle
    : poolSummary.state === 'starting' || poolSummary.state === 'loading'
      ? Loader2
      : Server;

  return (
    <div className="sidebar-status-bar">
      <AnimatePresence initial={false}>
        {showSearchRow && (
          <motion.div
            key="search-index-status"
            className="sidebar-status-wrapper"
            initial={{ maxHeight: 0, opacity: 0 }}
            animate={{ maxHeight: 40, opacity: 1 }}
            exit={{ maxHeight: 0, opacity: 0 }}
            transition={{ duration: 0.2, ease: 'easeOut' }}
          >
            <div
              className={`sidebar-status-row ${searchSummary.state} ${searchClickable ? 'clickable' : ''}`}
              role={searchClickable ? 'button' : 'status'}
              tabIndex={searchClickable ? 0 : undefined}
              onClick={searchClickable ? () => reindex() : undefined}
              onKeyDown={searchClickable ? (event) => event.key === 'Enter' && reindex() : undefined}
              aria-live="polite"
            >
              <SearchIcon size={14} className={searchSummary.state === 'indexing' ? 'sidebar-status-spinner' : ''} />
              <span className="sidebar-status-text">{searchSummary.text}</span>
              {searchSummary.meta && <span className="sidebar-status-meta">{searchSummary.meta}</span>}
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      <AnimatePresence initial={false}>
        {showPoolRow && (
          <motion.div
            key="pool-status"
            className="sidebar-status-wrapper"
            initial={{ maxHeight: 0, opacity: 0 }}
            animate={{ maxHeight: 40, opacity: 1 }}
            exit={{ maxHeight: 0, opacity: 0 }}
            transition={{ duration: 0.2, ease: 'easeOut' }}
          >
            <div
              className={`sidebar-status-row ${poolSummary.state} ${poolClickable ? 'clickable' : ''}`}
              role={poolClickable ? 'button' : 'status'}
              tabIndex={poolClickable ? 0 : undefined}
              onClick={poolClickable ? () => refreshPool() : undefined}
              onKeyDown={poolClickable ? (event) => event.key === 'Enter' && refreshPool() : undefined}
              aria-live="polite"
            >
              <PoolIcon size={14} className={poolSummary.state === 'starting' || poolSummary.state === 'loading' ? 'sidebar-status-spinner' : ''} />
              <span className="sidebar-status-text">{poolSummary.text}</span>
              {poolSummary.meta && <span className="sidebar-status-meta">{poolSummary.meta}</span>}
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      <UpdateNotification />
    </div>
  );
}
