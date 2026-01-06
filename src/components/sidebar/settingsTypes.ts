export type MCPDef = {
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
