// ABOUTME: Reusable toggle row component with label, description, and switch.
// ABOUTME: Used throughout settings for consistent toggle styling.

import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';

interface ToggleRowProps {
  id: string;
  label: string;
  description: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
}

export function ToggleRow({
  id,
  label,
  description,
  checked,
  onCheckedChange,
  disabled = false,
}: ToggleRowProps) {
  const descriptionId = `${id}-description`;

  return (
    <div className="flex items-center justify-between py-3">
      <div className="space-y-1 pr-4">
        <Label
          htmlFor={id}
          className="text-sm font-medium cursor-pointer"
        >
          {label}
        </Label>
        <p
          id={descriptionId}
          className="text-xs text-muted-foreground leading-relaxed"
        >
          {description}
        </p>
      </div>
      <Switch
        id={id}
        checked={checked}
        onCheckedChange={onCheckedChange}
        disabled={disabled}
        aria-describedby={descriptionId}
      />
    </div>
  );
}
