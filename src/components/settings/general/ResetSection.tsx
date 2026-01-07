// ABOUTME: Reset and maintenance section with action cards.
// ABOUTME: Provides options to reset settings and clear cache.

import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Wrench, RotateCcw, Eraser } from 'lucide-react';
import { ResetActionCard } from './ResetActionCard';

interface ResetSectionProps {
  onResetSettings: () => Promise<void>;
  onClearCache: () => Promise<void>;
}

export function ResetSection({
  onResetSettings,
  onClearCache,
}: ResetSectionProps) {
  return (
    <Card>
      <CardHeader className="pb-4">
        <CardTitle className="text-base flex items-center gap-2">
          <Wrench size={16} className="text-muted-foreground" />
          Reset & Maintenance
        </CardTitle>
        <CardDescription>
          Start fresh or free up space
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-4">
          <ResetActionCard
            icon={<RotateCcw size={18} />}
            title="Reset Settings"
            description="Restore all settings to their defaults"
            buttonLabel="Reset"
            confirmMessage="This will reset all your preferences"
            onAction={onResetSettings}
          />
          <ResetActionCard
            icon={<Eraser size={18} />}
            title="Clear Cache"
            description="Free up storage space"
            buttonLabel="Clear"
            confirmMessage="Cached data will be removed"
            onAction={onClearCache}
          />
        </div>
      </CardContent>
    </Card>
  );
}
