// ABOUTME: Action card component for reset/clear operations.
// ABOUTME: Includes two-step inline confirmation for destructive actions.

import { useState, useEffect, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Loader2 } from 'lucide-react';

interface ResetActionCardProps {
  icon: ReactNode;
  title: string;
  description: string;
  buttonLabel: string;
  confirmMessage?: string;
  onAction: () => Promise<void>;
  variant?: 'outline' | 'destructive';
}

export function ResetActionCard({
  icon,
  title,
  description,
  buttonLabel,
  confirmMessage,
  onAction,
  variant = 'outline',
}: ResetActionCardProps) {
  const [confirming, setConfirming] = useState(false);
  const [loading, setLoading] = useState(false);

  // Auto-revert confirmation after 3 seconds
  useEffect(() => {
    if (confirming) {
      const timer = setTimeout(() => {
        setConfirming(false);
      }, 3000);
      return () => clearTimeout(timer);
    }
  }, [confirming]);

  const handleInitialClick = () => {
    setConfirming(true);
  };

  const handleCancel = () => {
    setConfirming(false);
  };

  const handleConfirm = async () => {
    setLoading(true);
    try {
      await onAction();
    } finally {
      setLoading(false);
      setConfirming(false);
    }
  };

  return (
    <div className="p-4 rounded-lg border bg-card space-y-3">
      <div className="flex items-start gap-3">
        <div className="text-muted-foreground shrink-0 mt-0.5">
          {icon}
        </div>
        <div className="space-y-1 min-w-0">
          <h3 className="text-sm font-medium">{title}</h3>
          <p className="text-xs text-muted-foreground leading-relaxed">
            {description}
          </p>
        </div>
      </div>

      {!confirming ? (
        <Button
          variant={variant}
          size="sm"
          onClick={handleInitialClick}
          disabled={loading}
          className="w-full"
        >
          {buttonLabel}
        </Button>
      ) : (
        <div className="space-y-2">
          {confirmMessage && (
            <p className="text-xs text-muted-foreground text-center">
              {confirmMessage}
            </p>
          )}
          <div className="flex gap-2">
            <Button
              variant="ghost"
              size="sm"
              onClick={handleCancel}
              disabled={loading}
              className="flex-1"
            >
              Cancel
            </Button>
            <Button
              variant="destructive"
              size="sm"
              onClick={handleConfirm}
              disabled={loading}
              className="flex-1"
              aria-label={`Confirm ${title.toLowerCase()}. This cannot be undone.`}
            >
              {loading ? (
                <Loader2 className="animate-spin" size={14} />
              ) : (
                'Confirm'
              )}
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
