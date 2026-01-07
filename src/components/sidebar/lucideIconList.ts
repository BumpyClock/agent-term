/**
 * Lucide icon metadata and search utilities.
 * Generates a searchable list from lucide-react's dynamicIconImports.
 */

import dynamicIconImports from 'lucide-react/dynamicIconImports';

export interface LucideIconMeta {
  name: string;
  displayName: string;
}

function toDisplayName(name: string): string {
  return name
    .split('-')
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}

export const lucideIconList: LucideIconMeta[] = Object.keys(dynamicIconImports)
  .map((name) => ({
    name,
    displayName: toDisplayName(name),
  }))
  .sort((a, b) => a.displayName.localeCompare(b.displayName));

export function searchLucideIcons(query: string): LucideIconMeta[] {
  if (!query.trim()) {
    return lucideIconList;
  }
  const lower = query.toLowerCase();
  return lucideIconList.filter(
    (icon) =>
      icon.name.includes(lower) ||
      icon.displayName.toLowerCase().includes(lower)
  );
}
