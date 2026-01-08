// ABOUTME: Accent color selection section with pastel color swatches.
// ABOUTME: Allows users to customize the app's primary accent color.

import { Paintbrush } from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { accentColors, type AccentColorId, applyAccentColor } from '@/lib/accentColors';
import { cn } from '@/lib/utils';
import { useEffect } from 'react';

interface AccentColorSectionProps {
  accentColor: AccentColorId;
  onAccentColorChange: (color: AccentColorId) => void;
}

export function AccentColorSection({ accentColor, onAccentColorChange }: AccentColorSectionProps) {
  // Apply accent color on mount and when it changes
  useEffect(() => {
    applyAccentColor(accentColor);
  }, [accentColor]);

  const handleColorSelect = (colorId: AccentColorId) => {
    onAccentColorChange(colorId);
    applyAccentColor(colorId);
  };

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base flex items-center gap-2">
          <Paintbrush size={16} className="text-muted-foreground" />
          Accent Color
        </CardTitle>
        <CardDescription>Choose your preferred accent color</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-3">
          <Label>Color</Label>
          <div className="flex gap-3">
            {Object.values(accentColors).map((color) => (
              <button
                key={color.id}
                type="button"
                onClick={() => handleColorSelect(color.id)}
                className={cn(
                  "w-10 h-10 rounded-lg border-2 transition-all duration-150",
                  "hover:scale-110 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-offset-background",
                  accentColor === color.id
                    ? "border-foreground ring-2 ring-foreground/20"
                    : "border-transparent"
                )}
                style={{ backgroundColor: color.hex }}
                title={`${color.name} - ${color.description}`}
                aria-label={`Select ${color.name} accent color`}
                aria-pressed={accentColor === color.id}
              />
            ))}
          </div>
          <p className="text-xs text-muted-foreground mt-2">
            {accentColors[accentColor].name} — {accentColors[accentColor].description}
          </p>
        </div>
      </CardContent>
    </Card>
  );
}
