// Tool info returned from backend (tools_list command)
export interface ToolInfo {
  id: string;
  name: string;
  command: string;
  args: string[];
  icon: string;
  description: string;
  isShell: boolean;
  order: number;
  enabled: boolean;
  isBuiltin: boolean;
}

// Tool definition for settings (matches backend ToolDefDto)
export interface ToolDef {
  command: string;
  args: string[];
  icon: string;
  description: string;
  busyPatterns: string[];
  isShell: boolean;
  order: number;
  enabled: boolean;
}

// Shell settings (matches backend ShellSettingsDto)
export interface ShellSettings {
  defaultShell: string;
  defaultShellArgs: string[];
}

// Settings payload (matches backend ToolsSettingsDto)
export interface ToolsSettings {
  tools: Record<string, ToolDef>;
  shell: ShellSettings;
}
