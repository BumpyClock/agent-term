// ABOUTME: Displays a badge indicating when a session is open in multiple windows.
// ABOUTME: Polls subscriber count and shows mirror icon with count when session is mirrored.

import { useState, useEffect } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { cn } from '@/lib/utils';

interface MirrorBadgeProps {
  sessionId: string;
  className?: string;
}

/**
 * MirrorBadge shows when a terminal session is being viewed in multiple windows.
 * It polls the backend for subscriber count and displays the count when > 1.
 *
 * Example:
 * ```tsx
 * <MirrorBadge sessionId="abc-123" />
 * ```
 */
export function MirrorBadge({ sessionId, className }: MirrorBadgeProps) {
  const [count, setCount] = useState(0);

  useEffect(() => {
    let mounted = true;

    const fetchCount = async () => {
      try {
        const subscriberCount = await invoke<number>('get_session_subscriber_count', { sessionId });
        if (mounted) {
          setCount(subscriberCount);
        }
      } catch {
        // Session not running or error - show no badge
        if (mounted) {
          setCount(0);
        }
      }
    };

    // Initial fetch
    fetchCount();

    // Poll every second for subscriber count updates
    const interval = setInterval(fetchCount, 1000);

    return () => {
      mounted = false;
      clearInterval(interval);
    };
  }, [sessionId]);

  // Only show badge when session is open in 2+ windows
  if (count <= 1) {
    return null;
  }

  return (
    <span
      className={cn('mirror-badge', className)}
      title={`Open in ${count} windows`}
    >
      &#9680;{count}
    </span>
  );
}
