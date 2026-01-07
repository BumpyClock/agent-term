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
  // New fields for enhanced shell management
  defaultShellId?: string;           // ID of default shell (empty = auto-detect)
  shellOverrides?: ShellOverride[];  // Per-shell argument customizations
  customShells?: CustomShell[];      // User-added shells
}

// Per-shell customization (overrides detected shell defaults)
export interface ShellOverride {
  shellId: string;      // Reference to detected shell ID
  args: string[];       // Custom arguments (overrides default)
  disabled: boolean;    // Hide from tab picker
}

// User-added custom shell
export interface CustomShell {
  id: string;
  name: string;
  command: string;
  args: string[];
  icon: string;
  enabled: boolean;
}

// Settings payload (matches backend ToolsSettingsDto)
export interface ToolsSettings {
  tools: Record<string, ToolDef>;
  shell: ShellSettings;
}
