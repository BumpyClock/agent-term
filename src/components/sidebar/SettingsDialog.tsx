import { useCallback, useEffect, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { Button } from '@/components/ui/button';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { AppearanceSettings, ToolsSettings, GeneralSettings } from '@/components/settings';
import type { ToolItem } from '@/components/settings';
import type { ToolsSettings as ToolsSettingsPayload, ShellSettings, ToolDef } from '@/types/tools';

type SettingsDialogProps = {
  onClose: () => void;
};

const emptyShell: ShellSettings = {
  defaultShell: '',
  defaultShellArgs: [],
};

export function SettingsDialog({ onClose }: SettingsDialogProps) {
  const [activeTab, setActiveTab] = useState('general');
  const [tools, setTools] = useState<ToolItem[]>([]);
  const [shell, setShell] = useState<ShellSettings>(emptyShell);
  const [detectedShell, setDetectedShell] = useState('');
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState('');
  const [toolsView, setToolsView] = useState<'list' | 'form'>('list');

  const loadSettings = useCallback(async () => {
    setIsLoading(true);
    setError('');
    try {
      const [payload, detected] = await Promise.all([
        invoke<ToolsSettingsPayload>('tools_get_settings'),
        invoke<string>('get_default_shell'),
      ]);

      const toolItems: ToolItem[] = Object.entries(payload.tools).map(([name, def]) => ({
        id: name,
        name,
        ...def,
      }));

      setTools(toolItems);
      setShell(payload.shell);
      setDetectedShell(detected);
    } catch (err) {
      console.error('Failed to load tools settings:', err);
      setError('Failed to load settings');
      setTools([]);
      setShell(emptyShell);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  const handleSave = async () => {
    setIsSaving(true);
    setError('');
    try {
      const toolsMap: Record<string, ToolDef> = {};
      tools.forEach((tool) => {
        toolsMap[tool.name] = {
          command: tool.command,
          args: tool.args,
          icon: tool.icon,
          description: tool.description,
          busyPatterns: tool.busyPatterns || [],
          isShell: tool.isShell,
          order: tool.order,
          enabled: tool.enabled,
        };
      });

      await invoke('tools_set_settings', {
        settings: { tools: toolsMap, shell },
      });

      onClose();
    } catch (err) {
      console.error('Failed to save tools settings:', err);
      setError('Failed to save settings');
    } finally {
      setIsSaving(false);
    }
  };

  const showSaveButton = activeTab === 'tools' && toolsView === 'list';

  return (
    <div className="dialog-overlay" onClick={onClose}>
      <div className="settings-dialog" onClick={(event) => event.stopPropagation()}>
        <div className="flex-shrink-0 pb-4">
          <h2 className="text-xl font-semibold">Settings</h2>
        </div>

        <Tabs value={activeTab} onValueChange={setActiveTab} className="flex-1 flex flex-col min-h-0">
          <TabsList className="flex-shrink-0 mb-4">
            <TabsTrigger value="general">General</TabsTrigger>
            <TabsTrigger value="appearance">Appearance</TabsTrigger>
            <TabsTrigger value="tools">Tools & Shells</TabsTrigger>
          </TabsList>

          <TabsContent value="general" className="flex-1 overflow-y-auto">
            <GeneralSettings />
          </TabsContent>

          <TabsContent value="appearance" className="flex-1 overflow-y-auto">
            <AppearanceSettings />
          </TabsContent>

          <TabsContent value="tools" className="flex-1 overflow-y-auto">
            {isLoading ? (
              <div className="text-muted-foreground">Loading settings...</div>
            ) : (
              <ToolsSettings
                tools={tools}
                shell={shell}
                detectedShell={detectedShell}
                onToolsChange={setTools}
                onShellChange={setShell}
                onViewChange={setToolsView}
              />
            )}
          </TabsContent>
        </Tabs>

        {error && <div className="text-destructive text-sm flex-shrink-0 pt-2">{error}</div>}

        <div className="flex justify-end gap-3 pt-4 flex-shrink-0">
          <Button variant="outline" onClick={onClose}>
            {showSaveButton ? 'Cancel' : 'Close'}
          </Button>
          {showSaveButton && (
            <Button onClick={handleSave} disabled={isSaving}>
              Save settings
            </Button>
          )}
        </div>
      </div>
    </div>
  );
}
