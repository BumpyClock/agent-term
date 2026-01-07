// ABOUTME: Exposes selectable tool icons for the sidebar and icon picker.
// ABOUTME: Provides helper metadata for optional monochrome handling.

/**
 * Tool icon options for the icon picker.
 * Files in public/tool-icons/ are served at /tool-icons/ URL.
 */

export interface ToolIconOption {
  label: string;
  value: string;
  monochrome?: boolean;
}

export const toolIconOptions: ToolIconOption[] = [
  { label: 'Anthropic', value: '/tool-icons/anthropic-logo.svg', monochrome: true },
  { label: 'Arch Linux', value: '/tool-icons/archlinux.svg', monochrome: true },
  { label: 'Claude', value: '/tool-icons/claude.svg' },
  { label: 'Claude Logo', value: '/tool-icons/claude-logo.svg' },
  { label: 'Cursor', value: '/tool-icons/cursor.svg' },
  { label: 'Git', value: '/tool-icons/git.svg' , monochrome: true },
  { label: 'Google', value: '/tool-icons/google-logo.svg' },
  { label: 'Gemini', value: '/tool-icons/googlegemini.svg' },
  { label: 'Grok', value: '/tool-icons/Grok.png', monochrome: true },
  { label: 'MCP', value: '/tool-icons/mcp.svg', monochrome: true },
  { label: 'Ollama', value: '/tool-icons/Ollama.png' },
  { label: 'OpenAI', value: '/tool-icons/openai.svg', monochrome: true },
  { label: 'OpenRouter', value: '/tool-icons/OpenRouter.png' },
  { label: 'Python', value: '/tool-icons/Python-logo-notext.svg' },
  { label: 'React', value: '/tool-icons/React-icon.svg' },
  { label: 'Ubuntu', value: '/tool-icons/ubuntu.svg' },
  { label: 'VS Code', value: '/tool-icons/Visual_Studio_Code_1.35_icon.svg' },
  { label: 'Windsurf', value: '/tool-icons/windsurf-white-symbol.svg' },
];

export function getToolIconOptions(): ToolIconOption[] {
  return toolIconOptions;
}

const monochromeIconValues = new Set(
  toolIconOptions.filter((option) => option.monochrome).map((option) => option.value)
);

export function isMonochromeToolIcon(value: string | null | undefined): boolean {
  if (!value) return false;
  return monochromeIconValues.has(value);
}
