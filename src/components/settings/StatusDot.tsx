import type { ServerStatus } from '@/components/sidebar/settingsTypes';

type StatusDotProps = {
  status: ServerStatus;
  size?: 'sm' | 'md';
  showLabel?: boolean;
};

const statusColors: Record<ServerStatus, string> = {
  Running: 'bg-green-500',
  Starting: 'bg-yellow-500 animate-pulse',
  Stopped: 'bg-gray-400',
  Failed: 'bg-red-500',
};

const statusLabels: Record<ServerStatus, string> = {
  Running: 'Running',
  Starting: 'Starting...',
  Stopped: 'Stopped',
  Failed: 'Failed',
};

export function StatusDot({ status, size = 'sm', showLabel = false }: StatusDotProps) {
  const sizeClasses = size === 'sm' ? 'w-2 h-2' : 'w-3 h-3';

  return (
    <span className="inline-flex items-center gap-1.5">
      <span
        className={`${sizeClasses} rounded-full ${statusColors[status]}`}
        title={statusLabels[status]}
      />
      {showLabel && (
        <span className="text-xs text-muted-foreground">{statusLabels[status]}</span>
      )}
    </span>
  );
}
