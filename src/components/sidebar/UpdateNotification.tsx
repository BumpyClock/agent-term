// ABOUTME: Displays update notification at the bottom of the sidebar.
// ABOUTME: Shows update availability, download progress, and restart prompt.

import { Download, RefreshCw, Loader2, AlertCircle } from 'lucide-react';
import { useUpdateStore } from '@/store/updateStore';
import { getErrorMessage } from '@/types/update';
import './Sidebar.css';

export function UpdateNotification() {
  const { status, updateInfo, downloadProgress, downloadUpdate, installUpdate, error, retryCheck } = useUpdateStore();

  // Don't render if no update activity
  if (status === 'idle' || status === 'checking') {
    return null;
  }

  // Error state
  if (status === 'error' && error) {
    return (
      <div
        className="update-notification error clickable"
        onClick={retryCheck}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => e.key === 'Enter' && retryCheck()}
      >
        <AlertCircle size={14} />
        <span className="update-notification-text">{getErrorMessage(error)}</span>
        <span className="update-notification-retry">Retry</span>
      </div>
    );
  }

  const handleClick = async () => {
    if (status === 'available') {
      await downloadUpdate();
    } else if (status === 'ready') {
      await installUpdate();
    }
  };

  const getContent = () => {
    switch (status) {
      case 'available':
        return (
          <>
            <Download size={14} />
            <span className="update-notification-text">
              Update {updateInfo?.version} available
            </span>
          </>
        );
      case 'downloading':
        return (
          <>
            <Loader2 size={14} className="update-notification-spinner" />
            <span className="update-notification-text">
              Downloading... {Math.round(downloadProgress)}%
            </span>
            <div
              className="update-notification-progress"
              style={{ width: `${downloadProgress}%` }}
            />
          </>
        );
      case 'ready':
        return (
          <>
            <RefreshCw size={14} />
            <span className="update-notification-text">
              Restart to apply update
            </span>
          </>
        );
      default:
        return null;
    }
  };

  const isClickable = status === 'available' || status === 'ready';

  return (
    <div
      className={`update-notification ${status} ${isClickable ? 'clickable' : ''}`}
      onClick={isClickable ? handleClick : undefined}
      role={isClickable ? 'button' : undefined}
      tabIndex={isClickable ? 0 : undefined}
      onKeyDown={isClickable ? (e) => e.key === 'Enter' && handleClick() : undefined}
    >
      {getContent()}
    </div>
  );
}
