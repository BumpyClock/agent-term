export type MCPDef = {
  enabled: boolean;
  command: string;
  args: string[];
  env: Record<string, string>;
  description: string;
  url: string;
  transport: string;
};

export type MCPPoolSettings = {
  enabled: boolean;
  autoStart: boolean;
  portStart: number;
  portEnd: number;
  startOnDemand: boolean;
  shutdownOnExit: boolean;
  poolMcps: string[];
  fallbackToStdio: boolean;
  showPoolStatus: boolean;
  poolAll: boolean;
  excludeMcps: string[];
};

export type ServerStatus = 'Stopped' | 'Starting' | 'Running' | 'Failed';

export type McpServerStatus = {
  name: string;
  status: ServerStatus;
  socketPath: string;
  uptimeSeconds: number | null;
  connectionCount: number;
  owned: boolean;
};

export type PoolStatusResponse = {
  enabled: boolean;
  serverCount: number;
  servers: McpServerStatus[];
};
