// ABOUTME: Defines shared sidebar types used by components and utils.
// ABOUTME: Includes icon descriptors and search result shapes.
export interface SearchResult {
  filePath: string;
  projectName: string;
  messageType: string;
  timestamp: string | null;
  snippet: string;
  matchPositions: [number, number][];
  score: number;
}

export type IconDescriptor =
  | { kind: 'img'; src: string; monochrome?: boolean }
  | { kind: 'lucide'; id: string };

export type PopoverPosition = { x: number; y: number };

export type McpScope = 'global' | 'local';
