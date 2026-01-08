// ABOUTME: Accent color definitions for the app theme.
// ABOUTME: Provides pastel accent colors that work in both light and dark modes.

export type AccentColorId = "periwinkle" | "dusty-rose" | "sage-green" | "soft-teal";

export interface AccentColor {
  id: AccentColorId;
  name: string;
  oklch: string;
  hex: string;
  description: string;
}

export const accentColors: Record<AccentColorId, AccentColor> = {
  periwinkle: {
    id: "periwinkle",
    name: "Periwinkle",
    oklch: "oklch(0.68 0.12 270)",
    hex: "#9D8FD4",
    description: "Soft blue-violet, calming",
  },
  "dusty-rose": {
    id: "dusty-rose",
    name: "Dusty Rose",
    oklch: "oklch(0.70 0.10 10)",
    hex: "#D4A5A5",
    description: "Warm pink-mauve",
  },
  "sage-green": {
    id: "sage-green",
    name: "Sage Green",
    oklch: "oklch(0.68 0.08 145)",
    hex: "#8FBC8F",
    description: "Natural, easy on eyes",
  },
  "soft-teal": {
    id: "soft-teal",
    name: "Soft Teal",
    oklch: "oklch(0.70 0.10 195)",
    hex: "#7FBFBF",
    description: "Professional, calming",
  },
};

/**
 * Get all accent colors as an array for UI rendering.
 */
export function getAccentColorOptions(): AccentColor[] {
  return Object.values(accentColors);
}

/**
 * Apply an accent color to the document root CSS variables.
 */
export function applyAccentColor(colorId: AccentColorId): void {
  const color = accentColors[colorId];
  if (!color) return;

  const root = document.documentElement;

  // Set the primary/accent color variables
  root.style.setProperty("--primary", color.oklch);
  root.style.setProperty("--ring", color.oklch);
  root.style.setProperty("--sidebar-primary", color.oklch);
  root.style.setProperty("--sidebar-ring", color.oklch);

  // For light theme, primary-foreground should be dark text
  // For dark theme, primary-foreground should be light text
  // This is handled by the existing CSS, so we just update the primary color
}

/**
 * Get the default accent color ID.
 */
export function getDefaultAccentColor(): AccentColorId {
  return "periwinkle";
}
