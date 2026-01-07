import type { SessionTool } from '../../store/terminalStore';

export const toolOptions: Array<{
  tool: SessionTool;
  title: string;
  icon?: string;
}> = [
  { tool: 'shell', title: 'Shell' },
  { tool: 'claude', title: 'Claude Code', icon: '/tool-icons/claude-logo.svg' },
  { tool: 'codex', title: 'Codex', icon: '/tool-icons/OpenAI.png' },
  { tool: 'openCode', title: 'OpenCode', icon: '/tool-icons/Visual_Studio_Code_1.35_icon.svg' },
  { tool: 'gemini', title: 'Gemini', icon: '/tool-icons/google-logo.svg' },
];

export const lucideIcons = [
  {
    id: 'terminal',
    label: 'Terminal',
    svg: (
      <>
        <polyline points="4 17 10 11 4 5" />
        <line x1="12" y1="19" x2="20" y2="19" />
      </>
    ),
  },
  {
    id: 'code',
    label: 'Code',
    svg: (
      <>
        <polyline points="16 18 22 12 16 6" />
        <polyline points="8 6 2 12 8 18" />
      </>
    ),
  },
  {
    id: 'sparkles',
    label: 'Sparkles',
    svg: (
      <>
        <path d="M12 2l1.6 4.4L18 8l-4.4 1.6L12 14l-1.6-4.4L6 8l4.4-1.6L12 2z" />
        <path d="M5 16l0.8 2.2L8 19l-2.2 0.8L5 22l-0.8-2.2L2 19l2.2-0.8L5 16z" />
      </>
    ),
  },
  {
    id: 'bot',
    label: 'Bot',
    svg: (
      <>
        <rect x="3" y="6" width="18" height="12" rx="3" />
        <line x1="12" y1="3" x2="12" y2="6" />
        <circle cx="9" cy="12" r="1" />
        <circle cx="15" cy="12" r="1" />
      </>
    ),
  },
  {
    id: 'cpu',
    label: 'CPU',
    svg: (
      <>
        <rect x="7" y="7" width="10" height="10" rx="2" />
        <line x1="7" y1="1" x2="7" y2="5" />
        <line x1="17" y1="1" x2="17" y2="5" />
        <line x1="7" y1="19" x2="7" y2="23" />
        <line x1="17" y1="19" x2="17" y2="23" />
        <line x1="1" y1="7" x2="5" y2="7" />
        <line x1="1" y1="17" x2="5" y2="17" />
        <line x1="19" y1="7" x2="23" y2="7" />
        <line x1="19" y1="17" x2="23" y2="17" />
      </>
    ),
  },
  {
    id: 'zap',
    label: 'Zap',
    svg: <path d="M13 2L3 14h7l-1 8 10-12h-7l1-8z" />,
  },
];
