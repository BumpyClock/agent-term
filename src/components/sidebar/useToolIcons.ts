/**
 * Tool icon options for the icon picker.
 * Files in public/tool-icons/ are served at /tool-icons/ URL.
 */

export interface ToolIconOption {
  label: string;
  value: string;
}

export const toolIconOptions: ToolIconOption[] = [
  { label: 'Anthropic', value: '/tool-icons/anthropic-logo.svg' },
  { label: 'Arch Linux', value: '/tool-icons/archlinux.svg' },
  { label: 'Claude', value: '/tool-icons/claude.svg' },
  { label: 'Claude Logo', value: '/tool-icons/claude-logo.svg' },
  { label: 'Cursor', value: '/tool-icons/cursor.svg' },
  { label: 'Git', value: '/tool-icons/git.svg' },
  { label: 'Google', value: '/tool-icons/google-logo.svg' },
  { label: 'Gemini', value: '/tool-icons/googlegemini.svg' },
  { label: 'Grok', value: '/tool-icons/Grok.png' },
  { label: 'MCP', value: '/tool-icons/mcp.svg' },
  { label: 'Model Context Protocol', value: '/tool-icons/modelcontextprotocol.svg' },
  { label: 'Ollama', value: '/tool-icons/Ollama.png' },
  { label: 'OpenAI', value: '/tool-icons/OpenAI.png' },
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
