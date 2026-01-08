// ABOUTME: App information section displaying version, description, and links.
// ABOUTME: Shows app identity with warm, approachable styling (no card wrapper).

import { Button } from '@/components/ui/button';
import { Terminal, FileText, Github } from 'lucide-react';
import { useUpdateStore } from '@/store/updateStore';

const GITHUB_URL = 'https://github.com/BumpyClock/agent-term';
const RELEASES_URL = `${GITHUB_URL}/releases`;

export function AppInfoSection() {
  const { currentVersion } = useUpdateStore();

  return (
    <div className="pt-4 pb-2 border-t">
      <div className="flex items-start gap-4">
        {/* App Icon */}
        <div className="w-12 h-12 rounded-xl bg-linear-to-br from-primary/20 to-primary/5 flex items-center justify-center shrink-0">
          <Terminal className="w-6 h-6 text-primary" />
        </div>

        {/* App Info */}
        <div className="flex-1 space-y-1 min-w-0">
          <div>
            <h2 className="text-base font-bold">Agent Term</h2>
            <p className="text-xs text-muted-foreground">
              Version {currentVersion || '0.0.0'}
            </p>
          </div>
          <p className="text-xs text-muted-foreground leading-relaxed">
            A cross-platform terminal emulator for agentic coding workflows.
          </p>
        </div>

        {/* Action Links */}
        <div className="flex gap-1 shrink-0">
          <Button variant="ghost" size="sm" asChild>
            <a href={RELEASES_URL} target="_blank" rel="noopener noreferrer">
              <FileText size={14} className="mr-1.5" />
              Releases
            </a>
          </Button>
          <Button variant="ghost" size="sm" asChild>
            <a href={GITHUB_URL} target="_blank" rel="noopener noreferrer">
              <Github size={14} className="mr-1.5" />
              GitHub
            </a>
          </Button>
        </div>
      </div>
    </div>
  );
}
