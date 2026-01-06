import { useTheme } from '@/components/theme-provider';
import { Button } from '@/components/ui/button';
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
        <div className="flex gap-2">
          <Button
            variant={theme === 'system' ? 'default' : 'outline'}
            className="flex-1"
            onClick={() => setTheme('system')}
          >
            System
          </Button>
          <Button
            variant={theme === 'light' ? 'default' : 'outline'}
            className="flex-1"
            onClick={() => setTheme('light')}
          >
            Light
          </Button>
          <Button
            variant={theme === 'dark' ? 'default' : 'outline'}
            className="flex-1"
            onClick={() => setTheme('dark')}
          >
            Dark
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
