// ABOUTME: General settings tab containing update configuration.
// ABOUTME: Allows users to enable/disable auto-update and manually check for updates.

import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { useUpdateStore } from '@/store/updateStore';
import { getErrorMessage } from '@/types/update';
import { Loader2, CheckCircle, AlertCircle } from 'lucide-react';

export function GeneralSettings() {
  const {
    updateInfo,
    settings,
    isLoadingSettings,
    loadSettings,
    saveSettings,
    checkForUpdate,
    error,
    clearError,
  } = useUpdateStore();

  const [isChecking, setIsChecking] = useState(false);
  const [checkResult, setCheckResult] = useState<'none' | 'found' | 'up-to-date'>('none');

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  const handleAutoUpdateChange = async (checked: boolean) => {
    try {
      await saveSettings({ autoUpdate: checked });
    } catch (err) {
      console.error('Failed to save auto-update setting:', err);
    }
  };

  const handleCheckEnabledChange = async (checked: boolean) => {
    try {
      await saveSettings({ checkEnabled: checked });
    } catch (err) {
      console.error('Failed to save check-enabled setting:', err);
    }
  };

  const handleCheckNow = async () => {
    setIsChecking(true);
    setCheckResult('none');
    clearError();

    try {
      const result = await checkForUpdate();
      setCheckResult(result ? 'found' : 'up-to-date');
    } catch (err) {
      console.error('Check for update failed:', err);
    } finally {
      setIsChecking(false);
    }
  };

  if (isLoadingSettings) {
    return (
      <div className="flex items-center justify-center py-8 text-muted-foreground">
        <Loader2 className="animate-spin mr-2" size={16} />
        Loading settings...
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-base">Updates</CardTitle>
          <CardDescription>Configure how Agent Term checks for and installs updates</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="flex items-center justify-between">
            <div className="space-y-0.5">
              <Label htmlFor="auto-update" className="text-sm font-normal">
                Automatically download updates
              </Label>
              <p className="text-xs text-muted-foreground">
                When enabled, updates will download automatically in the background
              </p>
            </div>
            <Switch
              id="auto-update"
              checked={settings.autoUpdate}
              onCheckedChange={handleAutoUpdateChange}
            />
          </div>

          <div className="flex items-center justify-between">
            <div className="space-y-0.5">
              <Label htmlFor="check-enabled" className="text-sm font-normal">
                Check for updates
              </Label>
              <p className="text-xs text-muted-foreground">
                Periodically check for new versions
              </p>
            </div>
            <Switch
              id="check-enabled"
              checked={settings.checkEnabled}
              onCheckedChange={handleCheckEnabledChange}
            />
          </div>

          <div className="pt-2 flex items-center gap-3">
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
                'Check now'
              )}
            </Button>

            {checkResult === 'up-to-date' && (
              <span className="text-sm text-muted-foreground flex items-center gap-1">
                <CheckCircle size={14} className="text-green-500" />
                You're up to date
              </span>
            )}

            {checkResult === 'found' && updateInfo && (
              <span className="text-sm text-primary flex items-center gap-1">
                <AlertCircle size={14} />
                Version {updateInfo.version} available
              </span>
            )}

            {error && (
              <span className="text-sm text-destructive flex items-center gap-1">
                <AlertCircle size={14} />
                {getErrorMessage(error)}
              </span>
            )}
          </div>

          {settings.lastCheckTime && (
            <p className="text-xs text-muted-foreground pt-2">
              Last checked: {new Date(settings.lastCheckTime).toLocaleString()}
            </p>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
