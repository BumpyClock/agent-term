// ABOUTME: Displays version info and update notification at the bottom of the sidebar.
// ABOUTME: Always shows current version, with smooth transitions for update states.

import { Download, RefreshCw, Loader2, AlertCircle, CheckCircle } from 'lucide-react';
import { motion, AnimatePresence } from 'motion/react';
import { useUpdateStore } from '@/store/updateStore';
import { getErrorMessage } from '@/types/update';
import './Sidebar.css';

export function UpdateNotification() {
  const {
    status,
    updateInfo,
    downloadProgress,
    downloadUpdate,
    installUpdate,
    error,
    retryCheck,
    checkForUpdate,
    currentVersion,
    showUpToDate,
  } = useUpdateStore();

  const handleClick = async () => {
    if (status === 'idle' && !showUpToDate) {
      await checkForUpdate();
    } else if (status === 'available') {
      await downloadUpdate();
    } else if (status === 'ready') {
      await installUpdate();
    } else if (status === 'error') {
      await retryCheck();
    }
  };

  const isClickable = status === 'idle' || status === 'available' || status === 'ready' || status === 'error';
  const versionDisplay = currentVersion ? `v${currentVersion}` : '';

  // Determine which state to render
  const getStateKey = () => {
    if (status === 'checking') return 'checking';
    if (status === 'error') return 'error';
    if (status === 'available') return 'available';
    if (status === 'downloading') return 'downloading';
    if (status === 'ready') return 'ready';
    if (showUpToDate) return 'up-to-date';
    return 'idle';
  };

  const stateKey = getStateKey();

  const getContent = () => {
    switch (stateKey) {
      case 'idle':
        return (
          <motion.div
            key="idle"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <RefreshCw size={14} className="update-notification-refresh-icon" />
            <span className="update-notification-text">{versionDisplay}</span>
          </motion.div>
        );

      case 'checking':
        return (
          <motion.div
            key="checking"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <Loader2 size={14} className="update-notification-spinner" />
            <span className="update-notification-text">Checking...</span>
          </motion.div>
        );

      case 'up-to-date':
        return (
          <motion.div
            key="up-to-date"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <CheckCircle size={14} className="update-notification-check" />
            <span className="update-notification-text">
              {versionDisplay} <span className="update-notification-secondary">· Up to date</span>
            </span>
          </motion.div>
        );

      case 'available':
        return (
          <motion.div
            key="available"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <Download size={14} />
            <span className="update-notification-text">
              v{updateInfo?.version} available
            </span>
          </motion.div>
        );

      case 'downloading':
        return (
          <motion.div
            key="downloading"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <Loader2 size={14} className="update-notification-spinner" />
            <span className="update-notification-text">
              Downloading... <span className="update-notification-progress-text">{Math.round(downloadProgress)}%</span>
            </span>
            <motion.div
              className="update-notification-progress"
              initial={{ width: 0 }}
              animate={{ width: `${downloadProgress}%` }}
              transition={{ duration: 0.2, ease: 'easeOut' }}
            />
          </motion.div>
        );

      case 'ready':
        return (
          <motion.div
            key="ready"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <RefreshCw size={14} />
            <span className="update-notification-text">Restart to update</span>
          </motion.div>
        );

      case 'error':
        return (
          <motion.div
            key="error"
            className="update-notification-content"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.2 }}
          >
            <AlertCircle size={14} />
            <span className="update-notification-text">{getErrorMessage(error)}</span>
            <span className="update-notification-retry">Retry</span>
          </motion.div>
        );

      default:
        return null;
    }
  };

  return (
    <div
      className={`update-notification ${stateKey} ${isClickable ? 'clickable' : ''}`}
      onClick={isClickable ? handleClick : undefined}
      role={isClickable ? 'button' : 'status'}
      tabIndex={isClickable ? 0 : undefined}
      onKeyDown={isClickable ? (e) => e.key === 'Enter' && handleClick() : undefined}
      aria-live="polite"
    >
      <AnimatePresence mode="wait">
        {getContent()}
      </AnimatePresence>
    </div>
  );
}
