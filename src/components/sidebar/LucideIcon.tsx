import { lucideIcons } from './constants';

interface LucideIconProps {
  id: string;
  className?: string;
  title?: string;
}

export function LucideIcon({ id, className, title }: LucideIconProps) {
  const icon = lucideIcons.find((item) => item.id === id);
  if (!icon) return null;

  return (
    <span className={className} title={title}>
      <svg viewBox="0 0 24 24" aria-hidden="true">
        {icon.svg}
      </svg>
    </span>
  );
}
