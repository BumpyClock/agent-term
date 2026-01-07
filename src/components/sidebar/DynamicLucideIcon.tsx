/**
 * Lazy-loading Lucide icon component.
 * Uses React.lazy with dynamicIconImports for on-demand loading,
 * caching loaded components for performance.
 */

import { lazy, Suspense, ComponentType } from 'react';
import type { LucideProps } from 'lucide-react';
import dynamicIconImports from 'lucide-react/dynamicIconImports';

interface DynamicLucideIconProps extends Omit<LucideProps, 'ref'> {
  name: string;
  fallback?: React.ReactNode;
}

const iconCache = new Map<string, ComponentType<LucideProps>>();

function loadIcon(name: string): ComponentType<LucideProps> | null {
  if (iconCache.has(name)) {
    return iconCache.get(name)!;
  }

  const importFn = dynamicIconImports[name as keyof typeof dynamicIconImports];
  if (!importFn) {
    return null;
  }

  const LazyIcon = lazy(importFn);
  iconCache.set(name, LazyIcon);
  return LazyIcon;
}

export function DynamicLucideIcon({
  name,
  fallback,
  ...props
}: DynamicLucideIconProps) {
  const IconComponent = loadIcon(name);

  if (!IconComponent) {
    return fallback ? <>{fallback}</> : null;
  }

  return (
    <Suspense fallback={fallback || <span className="icon-placeholder" />}>
      <IconComponent {...props} />
    </Suspense>
  );
}
