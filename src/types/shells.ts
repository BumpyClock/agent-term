/**
 * Shell detection types for the shell picker
 */

/** Type of shell - native OS shell or WSL distribution */
export type ShellType = 'native' | 'wsl';

/** Information about an available shell */
export interface ShellInfo {
  /** Unique identifier for this shell */
  id: string;
  /** Display name */
  name: string;
  /** Command to execute (path or wsl command) */
  command: string;
  /** Default arguments for this shell */
  args: string[];
  /** Icon path */
  icon: string;
  /** Shell type (native or WSL) */
  shellType: ShellType;
  /** Whether this is the system default shell */
  isDefault: boolean;
}
