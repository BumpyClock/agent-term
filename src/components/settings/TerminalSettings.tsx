import { Label } from '@/components/ui/label';
import { Input } from '@/components/ui/input';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { useTerminalSettings, FONT_OPTIONS, DEFAULT_TERMINAL_SETTINGS } from '@/store/terminalSettingsStore';
import { Button } from '@/components/ui/button';

export function TerminalSettings() {
  const {
    fontFamily,
    fontSize,
    lineHeight,
    letterSpacing,
    setFontFamily,
    setFontSize,
    setLineHeight,
    setLetterSpacing,
    resetToDefaults,
  } = useTerminalSettings();

  const isCustomFont = !FONT_OPTIONS.some((opt) => opt.value === fontFamily);
  const hasChanges =
    fontFamily !== DEFAULT_TERMINAL_SETTINGS.fontFamily ||
    fontSize !== DEFAULT_TERMINAL_SETTINGS.fontSize ||
    lineHeight !== DEFAULT_TERMINAL_SETTINGS.lineHeight ||
    letterSpacing !== DEFAULT_TERMINAL_SETTINGS.letterSpacing;

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-base">Terminal Font</CardTitle>
            <CardDescription>Customize the terminal appearance</CardDescription>
          </div>
          {hasChanges && (
            <Button variant="ghost" size="sm" onClick={resetToDefaults}>
              Reset
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor="terminal-font">Font Family</Label>
            <NativeSelect
              id="terminal-font"
              value={isCustomFont ? 'custom' : fontFamily}
              onChange={(e) => {
                if (e.target.value !== 'custom') {
                  setFontFamily(e.target.value);
                }
              }}
            >
              {FONT_OPTIONS.map((opt) => (
                <NativeSelectOption key={opt.value} value={opt.value}>
                  {opt.label}
                </NativeSelectOption>
              ))}
              <NativeSelectOption value="custom">Custom...</NativeSelectOption>
            </NativeSelect>
          </div>

          <div className="space-y-2">
            <Label htmlFor="terminal-font-size">Font Size</Label>
            <Input
              id="terminal-font-size"
              type="number"
              min={8}
              max={32}
              value={fontSize}
              onChange={(e) => setFontSize(Number(e.target.value))}
            />
          </div>
        </div>

        {isCustomFont && (
          <div className="space-y-2">
            <Label htmlFor="terminal-custom-font">Custom Font Family</Label>
            <Input
              id="terminal-custom-font"
              value={fontFamily}
              onChange={(e) => setFontFamily(e.target.value)}
              placeholder='"MyFont", monospace'
            />
          </div>
        )}

        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor="terminal-line-height">Line Height</Label>
            <Input
              id="terminal-line-height"
              type="number"
              min={1.0}
              max={2.0}
              step={0.1}
              value={lineHeight}
              onChange={(e) => setLineHeight(Number(e.target.value))}
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor="terminal-letter-spacing">Letter Spacing (px)</Label>
            <Input
              id="terminal-letter-spacing"
              type="number"
              min={-2}
              max={5}
              step={0.5}
              value={letterSpacing}
              onChange={(e) => setLetterSpacing(Number(e.target.value))}
            />
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
