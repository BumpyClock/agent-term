// ABOUTME: List item component for displaying a custom tool.
// ABOUTME: Shows icon, name, command, shell badge, and enable toggle.

import { Switch } from '@/components/ui/switch';
import type { ToolItem } from '../ToolsSettings';

interface ToolListItemProps {
  tool: ToolItem;
  onToggle: (enabled: boolean) => void;
  onClick: () => void;
}

export function ToolListItem({ tool, onToggle, onClick }: ToolListItemProps) {
  return (
    <div
      className="flex items-center justify-between py-3 cursor-pointer hover:bg-muted/50 transition-colors -mx-3 px-3 rounded-md"
      onClick={onClick}
    >
      <div className="flex items-center gap-3 min-w-0">
        {tool.icon ? (
          <img src={tool.icon} alt="" className="w-5 h-5 shrink-0 object-contain" />
        ) : (
          <div className="w-5 h-5 rounded bg-muted flex items-center justify-center text-xs font-medium shrink-0">
            {tool.name.slice(0, 1).toUpperCase()}
          </div>
        )}
        <div className="min-w-0">
          <div className="font-medium truncate text-sm">{tool.name}</div>
          <div className="text-xs text-muted-foreground truncate flex items-center gap-1.5">
            <span>{tool.command}</span>
            {tool.isShell && (
              <>
                <span className="text-muted-foreground/50">•</span>
                <span className="text-muted-foreground">shell</span>
              </>
            )}
          </div>
        </div>
      </div>
      <Switch
        checked={tool.enabled}
        onClick={(e: React.MouseEvent) => e.stopPropagation()}
        onCheckedChange={onToggle}
      />
    </div>
  );
}
