// ABOUTME: Theme selection section with system/light/dark options.
// ABOUTME: Provides clear descriptions to help users understand each theme mode.

import { Palette } from 'lucide-react';
import { Label } from '@/components/ui/label';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';

interface ThemeSectionProps {
  theme: 'system' | 'light' | 'dark';
  onThemeChange: (theme: 'system' | 'light' | 'dark') => void;
}

export function ThemeSection({ theme, onThemeChange }: ThemeSectionProps) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base flex items-center gap-2">
          <Palette size={16} className="text-muted-foreground" />
          Appearance
        </CardTitle>
        <CardDescription>Choose how the app looks</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-2">
          <Label htmlFor="theme-select">Theme</Label>
          <NativeSelect
            id="theme-select"
            value={theme}
            onChange={(e) => onThemeChange(e.target.value as 'system' | 'light' | 'dark')}
          >
            <NativeSelectOption value="system">System</NativeSelectOption>
            <NativeSelectOption value="light">Light</NativeSelectOption>
            <NativeSelectOption value="dark">Dark</NativeSelectOption>
          </NativeSelect>
        </div>
      </CardContent>
    </Card>
  );
}
