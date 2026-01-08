// ABOUTME: Terminal color scheme definitions for xterm.js.
// ABOUTME: Schemes auto-map to light/dark variants based on app theme.

import type { ITheme } from "@xterm/xterm";

export type TerminalColorSchemeId = "one" | "nord" | "flexoki";

export interface TerminalColorSchemeGroup {
  id: TerminalColorSchemeId;
  name: string;
  hasLightVariant: boolean;
  light?: ITheme;
  dark: ITheme;
}

// Shared theme properties
const transparentBackground = "#00000000";

export const terminalColorSchemes: Record<TerminalColorSchemeId, TerminalColorSchemeGroup> = {
  one: {
    id: "one",
    name: "One",
    hasLightVariant: true,
    light: {
      background: transparentBackground,
      foreground: "#383A42",
      cursor: "#383A42",
      cursorAccent: "#FAFAFA",
      selectionBackground: "#E5E5E6",
      black: "#383A42",
      red: "#E45649",
      green: "#50A14F",
      yellow: "#986801",
      blue: "#4078F2",
      magenta: "#A626A4",
      cyan: "#0184BC",
      white: "#A0A1A7",
      brightBlack: "#4F525F",
      brightRed: "#E45649",
      brightGreen: "#50A14F",
      brightYellow: "#986801",
      brightBlue: "#4078F2",
      brightMagenta: "#A626A4",
      brightCyan: "#0184BC",
      brightWhite: "#FAFAFA",
    },
    dark: {
      background: transparentBackground,
      foreground: "#ABB2BF",
      cursor: "#ABB2BF",
      cursorAccent: "#282C34",
      selectionBackground: "#3E4452",
      black: "#282C34",
      red: "#E06C75",
      green: "#98C379",
      yellow: "#E5C07B",
      blue: "#61AFEF",
      magenta: "#C678DD",
      cyan: "#56B6C2",
      white: "#5C6370",
      brightBlack: "#3E4452",
      brightRed: "#E06C75",
      brightGreen: "#98C379",
      brightYellow: "#E5C07B",
      brightBlue: "#61AFEF",
      brightMagenta: "#C678DD",
      brightCyan: "#56B6C2",
      brightWhite: "#ABB2BF",
    },
  },
  nord: {
    id: "nord",
    name: "Nord",
    hasLightVariant: false,
    dark: {
      background: transparentBackground,
      foreground: "#D8DEE9",
      cursor: "#D8DEE9",
      cursorAccent: "#2E3440",
      selectionBackground: "#434C5E",
      black: "#3B4252",
      red: "#BF616A",
      green: "#A3BE8C",
      yellow: "#EBCB8B",
      blue: "#81A1C1",
      magenta: "#B48EAD",
      cyan: "#88C0D0",
      white: "#E5E9F0",
      brightBlack: "#4C566A",
      brightRed: "#BF616A",
      brightGreen: "#A3BE8C",
      brightYellow: "#EBCB8B",
      brightBlue: "#81A1C1",
      brightMagenta: "#B48EAD",
      brightCyan: "#8FBCBB",
      brightWhite: "#ECEFF4",
    },
  },
  flexoki: {
    id: "flexoki",
    name: "Flexoki",
    hasLightVariant: true,
    light: {
      background: transparentBackground,
      foreground: "#100F0F",
      cursor: "#100F0F",
      cursorAccent: "#FFFCF0",
      selectionBackground: "#E6E4D9",
      black: "#100F0F",
      red: "#AF3029",
      green: "#66800B",
      yellow: "#AD8301",
      blue: "#205EA6",
      magenta: "#A02F6F",
      cyan: "#24837B",
      white: "#6F6E69",
      brightBlack: "#575653",
      brightRed: "#AF3029",
      brightGreen: "#66800B",
      brightYellow: "#AD8301",
      brightBlue: "#205EA6",
      brightMagenta: "#A02F6F",
      brightCyan: "#24837B",
      brightWhite: "#FFFCF0",
    },
    dark: {
      background: transparentBackground,
      foreground: "#CECDC3",
      cursor: "#CECDC3",
      cursorAccent: "#100F0F",
      selectionBackground: "#403E3C",
      black: "#100F0F",
      red: "#D14D41",
      green: "#879A39",
      yellow: "#D0A215",
      blue: "#4385BE",
      magenta: "#CE5D97",
      cyan: "#3AA99F",
      white: "#878580",
      brightBlack: "#575653",
      brightRed: "#D14D41",
      brightGreen: "#879A39",
      brightYellow: "#D0A215",
      brightBlue: "#4385BE",
      brightMagenta: "#CE5D97",
      brightCyan: "#3AA99F",
      brightWhite: "#CECDC3",
    },
  },
};

/**
 * Get the terminal theme for a given scheme ID and app theme.
 * Auto-maps to the appropriate light/dark variant based on app theme.
 * Falls back to 'one' scheme if schemeId is invalid, and to dark variant if light is unavailable.
 */
export function getTerminalTheme(
  schemeId: TerminalColorSchemeId,
  appTheme: "light" | "dark"
): ITheme {
  // Fall back to 'one' if the scheme doesn't exist (handles old localStorage values)
  const scheme = terminalColorSchemes[schemeId] ?? terminalColorSchemes.one;

  if (appTheme === "light" && scheme.hasLightVariant && scheme.light) {
    return scheme.light;
  }

  return scheme.dark;
}

/**
 * Get all terminal color schemes as an array for UI rendering.
 */
export function getTerminalSchemeOptions(): Array<{
  id: TerminalColorSchemeId;
  name: string;
  hasLightVariant: boolean;
}> {
  return Object.values(terminalColorSchemes).map((scheme) => ({
    id: scheme.id,
    name: scheme.name,
    hasLightVariant: scheme.hasLightVariant,
  }));
}
