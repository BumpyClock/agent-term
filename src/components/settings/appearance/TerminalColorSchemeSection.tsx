// ABOUTME: Terminal color scheme selection section.
// ABOUTME: Schemes auto-map to light/dark variants based on app theme.

import { Terminal } from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import {
  getTerminalSchemeOptions,
  terminalColorSchemes,
  type TerminalColorSchemeId,
} from '@/lib/terminalThemes';

interface TerminalColorSchemeSectionProps {
  colorScheme: TerminalColorSchemeId;
  onColorSchemeChange: (scheme: TerminalColorSchemeId) => void;
}

export function TerminalColorSchemeSection({
  colorScheme,
  onColorSchemeChange,
}: TerminalColorSchemeSectionProps) {
  const options = getTerminalSchemeOptions();
  const currentScheme = terminalColorSchemes[colorScheme];

  // Use dark theme colors for preview (more representative)
  const previewColors = currentScheme?.dark;

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base flex items-center gap-2">
          <Terminal size={16} className="text-muted-foreground" />
          Terminal Colors
        </CardTitle>
        <CardDescription>Choose a color scheme for the terminal</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-3">
          <Label htmlFor="terminal-color-scheme">Color Scheme</Label>
          <NativeSelect
            id="terminal-color-scheme"
            value={colorScheme}
            onChange={(e) => onColorSchemeChange(e.target.value as TerminalColorSchemeId)}
          >
            {options.map((option) => (
              <NativeSelectOption key={option.id} value={option.id}>
                {option.name}
                {!option.hasLightVariant && ' (dark only)'}
              </NativeSelectOption>
            ))}
          </NativeSelect>

          {previewColors && (
            <div className="mt-3 p-3 rounded-lg border bg-card">
              <p className="text-xs text-muted-foreground mb-2">Color Preview</p>
              <div className="flex gap-1">
                {[
                  previewColors.black,
                  previewColors.red,
                  previewColors.green,
                  previewColors.yellow,
                  previewColors.blue,
                  previewColors.magenta,
                  previewColors.cyan,
                  previewColors.white,
                ].map((color, index) => (
                  <div
                    key={index}
                    className="w-5 h-5 rounded-sm"
                    style={{ backgroundColor: color as string }}
                    title={['Black', 'Red', 'Green', 'Yellow', 'Blue', 'Magenta', 'Cyan', 'White'][index]}
                  />
                ))}
              </div>
            </div>
          )}

          <p className="text-xs text-muted-foreground">
            {currentScheme?.hasLightVariant
              ? 'Automatically uses light variant in light mode.'
              : 'This scheme uses the same colors in both light and dark modes.'}
          </p>
        </div>
      </CardContent>
    </Card>
  );
}
