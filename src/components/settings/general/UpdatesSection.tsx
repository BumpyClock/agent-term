// ABOUTME: Updates configuration section with toggle switches and check button.
// ABOUTME: Manages auto-update and periodic check settings with status feedback.

import { useState } from 'react';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { RefreshCw, Loader2 } from 'lucide-react';
import { ToggleRow } from './ToggleRow';
import { UpdateStatusBanner } from './UpdateStatusBanner';
import type { UpdateSettings, UpdateInfo, UpdateError } from '@/types/update';

interface UpdatesSectionProps {
  settings: UpdateSettings;
  updateInfo: UpdateInfo | null;
  error: UpdateError | null;
  showUpToDate: boolean;
  onAutoUpdateChange: (checked: boolean) => Promise<void>;
  onCheckEnabledChange: (checked: boolean) => Promise<void>;
  onCheckNow: () => Promise<void>;
}

export function UpdatesSection({
  settings,
  updateInfo,
  error,
  showUpToDate,
  onAutoUpdateChange,
  onCheckEnabledChange,
  onCheckNow,
}: UpdatesSectionProps) {
  const [isChecking, setIsChecking] = useState(false);

  const handleCheckNow = async () => {
    setIsChecking(true);
    try {
      await onCheckNow();
    } finally {
      setIsChecking(false);
    }
  };

  // Determine banner state
  const getBannerState = () => {
    if (error) return 'error';
    if (updateInfo) return 'available';
    if (showUpToDate) return 'up-to-date';
    return 'idle';
  };

  return (
    <Card>
      <CardHeader className="pb-4">
        <CardTitle className="text-base flex items-center gap-2">
          <RefreshCw size={16} className="text-muted-foreground" />
          Updates
        </CardTitle>
        <CardDescription>
          Keep your app up to date
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-1">
        <ToggleRow
          id="auto-update"
          label="Auto-download updates"
          description="Updates download quietly in the background"
          checked={settings.autoUpdate}
          onCheckedChange={onAutoUpdateChange}
        />

        <div className="border-t my-1" />

        <ToggleRow
          id="check-enabled"
          label="Check for updates periodically"
          description="We'll let you know when something new arrives"
          checked={settings.checkEnabled}
          onCheckedChange={onCheckEnabledChange}
        />

        <div className="border-t my-1" />

        {/* Check Now Action */}
        <div className="pt-3">
          <Button
            variant="outline"
            size="sm"
            onClick={handleCheckNow}
            disabled={isChecking}
          >
            {isChecking ? (
              <>
                <Loader2 className="animate-spin mr-2" size={14} />
                Checking...
              </>
            ) : (
              'Check for updates'
            )}
          </Button>

          <UpdateStatusBanner
            state={getBannerState()}
            updateInfo={updateInfo}
            error={error}
            lastCheckTime={settings.lastCheckTime || null}
          />
        </div>
      </CardContent>
    </Card>
  );
}
