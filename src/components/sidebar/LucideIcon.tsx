import { lucideIcons } from './constants';
import { DynamicLucideIcon } from './DynamicLucideIcon';

interface LucideIconProps {
  id: string;
  className?: string;
  title?: string;
}

export function LucideIcon({ id, className, title }: LucideIconProps) {
  const staticIcon = lucideIcons.find((item) => item.id === id);

  if (staticIcon) {
    return (
      <span className={className} title={title}>
        <svg viewBox="0 0 24 24" aria-hidden="true">
          {staticIcon.svg}
        </svg>
      </span>
    );
  }

  return (
    <span className={className} title={title}>
      <DynamicLucideIcon name={id} />
    </span>
  );
}
