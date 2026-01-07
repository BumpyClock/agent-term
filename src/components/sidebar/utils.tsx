// ABOUTME: Provides sidebar utility helpers for rendering, icons, and status labels.
// ABOUTME: Normalizes icon descriptors and text highlights for sidebar components.
import type { ReactNode } from 'react';
import type { Session, SessionStatus, SessionTool, Section } from '../../store/terminalStore';
import { toolOptions } from './constants';
import type { IconDescriptor } from './types';
import { isMonochromeToolIcon } from './useToolIcons';

export function getStatusTitle(status: SessionStatus): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'waiting':
      return 'Waiting for input';
    case 'idle':
      return 'Idle';
    case 'error':
      return 'Error';
    case 'starting':
      return 'Starting';
    default:
      return 'Unknown';
  }
}

export function needsAttention(status: SessionStatus): boolean {
  return status === 'waiting' || status === 'error';
}

export function highlightMatches(text: string, matches: [number, number][]): ReactNode {
  if (!matches || matches.length === 0) {
    return text;
  }

  const parts: ReactNode[] = [];
  let lastIndex = 0;

  matches.forEach(([start, end], idx) => {
    if (start > lastIndex) {
      parts.push(text.slice(lastIndex, start));
    }
    parts.push(
      <mark key={idx} className="search-highlight">
        {text.slice(start, end)}
      </mark>
    );
    lastIndex = end;
  });

  if (lastIndex < text.length) {
    parts.push(text.slice(lastIndex));
  }

  return parts;
}

export function getToolIcon(tool: SessionTool): string | null {
  if (typeof tool !== 'string') return null;
  const match = toolOptions.find((option) => option.tool === tool);
  return match?.icon ?? null;
}

export function getToolTitle(tool: SessionTool): string {
  if (typeof tool !== 'string') return tool.custom;
  switch (tool) {
    case 'shell':
      return 'Shell';
    case 'claude':
      return 'Claude Code';
    case 'codex':
      return 'Codex';
    case 'openCode':
      return 'OpenCode';
    case 'gemini':
      return 'Gemini';
    default:
      return tool;
  }
}

export function resolveSessionIcon(session: Session): IconDescriptor | null {
  if (session.icon) {
    if (session.icon.startsWith('lucide:')) {
      return { kind: 'lucide', id: session.icon.slice('lucide:'.length) };
    }
    return { kind: 'img', src: session.icon, monochrome: isMonochromeToolIcon(session.icon) };
  }
  const fallback = getToolIcon(session.tool);
  if (fallback) {
    return { kind: 'img', src: fallback, monochrome: isMonochromeToolIcon(fallback) };
  }
  return null;
}

export function resolveSectionIcon(section: Section): IconDescriptor | null {
  if (!section.icon) return null;
  if (section.icon.startsWith('lucide:')) {
    return { kind: 'lucide', id: section.icon.slice('lucide:'.length) };
  }
  return { kind: 'img', src: section.icon, monochrome: isMonochromeToolIcon(section.icon) };
}
