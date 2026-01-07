// ABOUTME: List item component for displaying a shell in settings.
// ABOUTME: Shows icon, name, args preview, default badge, and enable toggle.

import { Switch } from '@/components/ui/switch';
import type { ShellInfo } from '@/types/shells';
import type { CustomShell } from '@/types/tools';

interface ShellListItemProps {
  shell: ShellInfo | CustomShell;
  isDefault: boolean;
  isEnabled: boolean;
  onToggle: (enabled: boolean) => void;
  onClick: () => void;
}

function getArgsDisplay(args: string[]): string {
  if (args.length === 0) return '(no args)';
  return args.join(', ');
}

function isCustomShell(shell: ShellInfo | CustomShell): shell is CustomShell {
  return !('shellType' in shell);
}

export function ShellListItem({
  shell,
  isDefault,
  isEnabled,
  onToggle,
  onClick,
}: ShellListItemProps) {
  const argsDisplay = getArgsDisplay(shell.args);
  const isCustom = isCustomShell(shell);

  return (
    <div
      className={`flex items-center justify-between py-3 cursor-pointer hover:bg-muted/50 transition-colors -mx-3 px-3 rounded-md ${
        !isEnabled ? 'opacity-50' : ''
      }`}
      onClick={onClick}
    >
      <div className="flex items-center gap-3 min-w-0">
        {shell.icon ? (
          <img src={shell.icon} alt="" className="w-5 h-5 shrink-0 object-contain" />
        ) : (
          <div className="w-5 h-5 rounded bg-muted flex items-center justify-center text-xs font-medium shrink-0">
            {shell.name.slice(0, 1).toUpperCase()}
          </div>
        )}
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="font-medium truncate text-sm">{shell.name}</span>
            {isDefault && (
              <span className="text-xs bg-primary/10 text-primary px-1.5 py-0.5 rounded shrink-0">
                default
              </span>
            )}
            {isCustom && (
              <span className="text-xs bg-muted text-muted-foreground px-1.5 py-0.5 rounded shrink-0">
                custom
              </span>
            )}
          </div>
          <div className="text-xs text-muted-foreground truncate">{argsDisplay}</div>
        </div>
      </div>
      <Switch
        checked={isEnabled}
        onClick={(e: React.MouseEvent) => e.stopPropagation()}
        onCheckedChange={onToggle}
      />
    </div>
  );
}
