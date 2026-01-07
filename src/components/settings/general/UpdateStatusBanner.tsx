// ABOUTME: Status banner component for update check feedback.
// ABOUTME: Shows contextual messages with animations for different states.

import { motion, AnimatePresence } from 'motion/react';
import { CheckCircle, AlertCircle, Info, Download } from 'lucide-react';
import type { UpdateInfo, UpdateError } from '@/types/update';
import { getErrorMessage } from '@/types/update';

type BannerState = 'idle' | 'up-to-date' | 'available' | 'error';

interface UpdateStatusBannerProps {
  state: BannerState;
  updateInfo: UpdateInfo | null;
  error: UpdateError | null;
  lastCheckTime: string | null;
  onDownload?: () => void;
}

function formatRelativeTime(isoString: string): string {
  const date = new Date(isoString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins} min ago`;
  if (diffHours < 24) return `${diffHours} hour${diffHours > 1 ? 's' : ''} ago`;
  return `${diffDays} day${diffDays > 1 ? 's' : ''} ago`;
}

export function UpdateStatusBanner({
  state,
  updateInfo,
  error,
  lastCheckTime,
}: UpdateStatusBannerProps) {
  // Don't render anything in idle state unless we have a last check time
  if (state === 'idle' && !lastCheckTime) {
    return null;
  }

  const getContent = () => {
    switch (state) {
      case 'up-to-date':
        return {
          icon: <CheckCircle className="text-green-500 shrink-0" size={18} />,
          title: "You're all set!",
          subtitle: lastCheckTime ? `Checked ${formatRelativeTime(lastCheckTime)}` : null,
          variant: 'success' as const,
        };

      case 'available':
        return {
          icon: <Download className="text-primary shrink-0" size={18} />,
          title: `Good news! Version ${updateInfo?.version} is ready`,
          subtitle: updateInfo?.body
            ? updateInfo.body.slice(0, 80) + (updateInfo.body.length > 80 ? '...' : '')
            : 'A new version is available for download',
          variant: 'info' as const,
        };

      case 'error':
        return {
          icon: <AlertCircle className="text-destructive shrink-0" size={18} />,
          title: error ? getErrorMessage(error) : 'Something went wrong',
          subtitle: 'Try again in a few moments',
          variant: 'error' as const,
        };

      case 'idle':
      default:
        return {
          icon: <Info className="text-muted-foreground shrink-0" size={16} />,
          title: null,
          subtitle: lastCheckTime ? `Last checked ${formatRelativeTime(lastCheckTime)}` : null,
          variant: 'neutral' as const,
        };
    }
  };

  const content = getContent();

  const variantStyles = {
    success: 'bg-green-500/10 border-green-500/20',
    info: 'bg-primary/10 border-primary/20',
    error: 'bg-destructive/10 border-destructive/20',
    neutral: 'bg-muted/50 border-transparent',
  };

  // For idle state with just last check time, show minimal version
  if (state === 'idle' && content.subtitle) {
    return (
      <p className="text-xs text-muted-foreground flex items-center gap-1.5">
        {content.icon}
        {content.subtitle}
      </p>
    );
  }

  return (
    <AnimatePresence mode="wait">
      <motion.div
        key={state}
        initial={{ opacity: 0, y: -8 }}
        animate={{ opacity: 1, y: 0 }}
        exit={{ opacity: 0, y: 8 }}
        transition={{ duration: 0.2, ease: 'easeOut' }}
        role="status"
        aria-live="polite"
        aria-atomic="true"
        className={`mt-4 p-4 rounded-lg border ${variantStyles[content.variant]}`}
      >
        <div className="flex items-start gap-3">
          {content.icon}
          <div className="space-y-0.5 min-w-0">
            {content.title && (
              <p className="text-sm font-medium leading-tight">{content.title}</p>
            )}
            {content.subtitle && (
              <p className="text-xs text-muted-foreground leading-relaxed">
                {content.subtitle}
              </p>
            )}
          </div>
        </div>
      </motion.div>
    </AnimatePresence>
  );
}
