import { ChevronRight } from 'lucide-react';
import { Switch } from '@/components/ui/switch';
import { StatusDot } from './StatusDot';
import type { ServerStatus } from '@/components/sidebar/settingsTypes';

type MCPServerListItemProps = {
  name: string;
  description: string;
  command: string;
  url: string;
  enabled: boolean;
  poolStatus?: ServerStatus;
  onToggle: (enabled: boolean) => void;
  onClick: () => void;
};

export function MCPServerListItem({
  name,
  description,
  command,
  url,
  enabled,
  poolStatus,
  onToggle,
  onClick,
}: MCPServerListItemProps) {
  const subtitle = description || command || url || 'No configuration';

  return (
    <div
      className="flex items-center gap-3 p-3 rounded-lg border bg-muted/50 hover:bg-muted transition-colors"
    >
      <Switch
        checked={enabled}
        onCheckedChange={onToggle}
        onClick={(e: React.MouseEvent) => e.stopPropagation()}
      />
      {poolStatus && <StatusDot status={poolStatus} />}
      <div
        className="flex-1 min-w-0 cursor-pointer"
        onClick={onClick}
      >
        <div className="font-medium truncate">{name || 'Unnamed MCP'}</div>
        <div className="text-sm text-muted-foreground truncate">{subtitle}</div>
      </div>
      <ChevronRight
        className="h-4 w-4 text-muted-foreground flex-shrink-0 cursor-pointer"
        onClick={onClick}
      />
    </div>
  );
}
