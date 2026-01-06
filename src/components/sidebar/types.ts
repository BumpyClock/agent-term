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
  | { kind: 'img'; src: string }
  | { kind: 'lucide'; id: string };

export type PopoverPosition = { x: number; y: number };
