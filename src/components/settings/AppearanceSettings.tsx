import { useTheme } from '@/components/theme-provider';
import { Label } from '@/components/ui/label';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';

export function AppearanceSettings() {
  const { theme, setTheme } = useTheme();

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base">Appearance</CardTitle>
        <CardDescription>Choose how the app looks</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-2">
          <Label htmlFor="theme-select">Theme</Label>
          <NativeSelect
            id="theme-select"
            value={theme}
            onChange={(e) => setTheme(e.target.value as 'system' | 'light' | 'dark')}
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
