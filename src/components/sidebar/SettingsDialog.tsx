import { Button } from '@/components/ui/button';
import { AppearanceSettings } from '@/components/settings';

type SettingsDialogProps = {
  onClose: () => void;
};

export function SettingsDialog({ onClose }: SettingsDialogProps) {
  return (
    <div className="dialog-overlay" onClick={onClose}>
      <div className="settings-dialog" onClick={(event) => event.stopPropagation()}>
        <div className="flex-shrink-0 pb-4">
          <h2 className="text-xl font-semibold">Settings</h2>
        </div>

        <div className="flex-1 flex flex-col min-h-0 overflow-y-auto">
          <AppearanceSettings />
        </div>

        <div className="flex justify-end gap-3 pt-4 flex-shrink-0">
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
        </div>
      </div>
    </div>
  );
}
