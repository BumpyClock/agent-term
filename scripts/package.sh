#!/bin/bash
set -e

# Agent Term Packaging Script
# Usage: ./scripts/package.sh [macos|macos-dmg|windows|linux-deb|linux-rpm|linux-appimage|all]

VERSION="0.1.4"
APP_NAME="agentterm"
DISPLAY_NAME="Agent Term"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

info() { echo -e "${GREEN}[INFO]${NC} $1"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
error() { echo -e "${RED}[ERROR]${NC} $1"; exit 1; }

check_tool() {
    if ! command -v "$1" &> /dev/null; then
        error "$1 is required but not installed. Install with: $2"
    fi
}

build_release() {
    info "Building release binary..."
    cargo build --release
}

package_macos() {
    info "Packaging for macOS (.app bundle)..."

    check_tool "cargo-bundle" "cargo install cargo-bundle"

    # Check for icon
    if [ ! -f "assets/AppIcon.icns" ]; then
        warn "assets/AppIcon.icns not found. Run './scripts/package.sh create-icon' first."
        warn "Continuing without icon..."
    fi

    cargo bundle --release

    info "macOS .app bundle created at: target/release/bundle/osx/${DISPLAY_NAME}.app"
}

package_macos_dmg() {
    package_macos

    info "Creating DMG installer..."

    check_tool "create-dmg" "brew install create-dmg"

    APP_PATH="target/release/bundle/osx/${DISPLAY_NAME}.app"
    DMG_PATH="target/release/${DISPLAY_NAME}-${VERSION}.dmg"

    # Remove existing DMG if present
    rm -f "$DMG_PATH"

    create-dmg \
        --volname "${DISPLAY_NAME}" \
        --volicon "assets/AppIcon.icns" \
        --window-pos 200 120 \
        --window-size 600 400 \
        --icon-size 100 \
        --icon "${DISPLAY_NAME}.app" 175 190 \
        --hide-extension "${DISPLAY_NAME}.app" \
        --app-drop-link 425 190 \
        "$DMG_PATH" \
        "$APP_PATH" || true  # create-dmg returns non-zero even on success sometimes

    if [ -f "$DMG_PATH" ]; then
        info "DMG created at: $DMG_PATH"
    else
        error "Failed to create DMG"
    fi
}

package_windows() {
    info "Packaging for Windows (.msi)..."

    check_tool "cargo-wix" "cargo install cargo-wix"

    # Initialize WiX if not already done
    if [ ! -d "wix" ]; then
        info "Initializing WiX configuration..."
        cargo wix init
    fi

    cargo wix --nocapture

    info "Windows .msi installer created in: target/wix/"
}

package_linux_deb() {
    info "Packaging for Debian/Ubuntu (.deb)..."

    check_tool "cargo-deb" "cargo install cargo-deb"

    # Check for required files
    if [ ! -f "agentterm.desktop" ]; then
        error "agentterm.desktop not found. Run './scripts/package.sh create-desktop' first."
    fi

    if [ ! -f "assets/agentterm.png" ]; then
        warn "assets/agentterm.png not found. Package will be created without icon."
    fi

    cargo deb

    info "Debian package created in: target/debian/"
}

package_linux_rpm() {
    info "Packaging for Fedora/RHEL (.rpm)..."

    check_tool "cargo-generate-rpm" "cargo install cargo-generate-rpm"

    build_release

    # Check for required files
    if [ ! -f "agentterm.desktop" ]; then
        error "agentterm.desktop not found. Run './scripts/package.sh create-desktop' first."
    fi

    cargo generate-rpm

    info "RPM package created in: target/generate-rpm/"
}

package_linux_appimage() {
    info "Packaging as AppImage..."

    LINUXDEPLOY="linuxdeploy-x86_64.AppImage"

    if [ ! -f "$LINUXDEPLOY" ]; then
        info "Downloading linuxdeploy..."
        wget -q "https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/${LINUXDEPLOY}"
        chmod +x "$LINUXDEPLOY"
    fi

    build_release

    # Create AppDir structure
    rm -rf AppDir
    mkdir -p AppDir/usr/bin
    mkdir -p AppDir/usr/share/icons/hicolor/256x256/apps
    mkdir -p AppDir/usr/share/applications

    cp "target/release/${APP_NAME}" AppDir/usr/bin/
    cp agentterm.desktop AppDir/usr/share/applications/

    if [ -f "assets/agentterm.png" ]; then
        cp assets/agentterm.png AppDir/usr/share/icons/hicolor/256x256/apps/
    fi

    ./${LINUXDEPLOY} --appdir AppDir --output appimage

    info "AppImage created: ${DISPLAY_NAME}-x86_64.AppImage"
}

create_desktop_file() {
    info "Creating .desktop file..."

    cat > agentterm.desktop << 'EOF'
[Desktop Entry]
Name=Agent Term
GenericName=Terminal Emulator
Comment=Terminal emulator optimized for Agentic coding workflows
Exec=agentterm %F
Icon=agentterm
Type=Application
Categories=Development;TerminalEmulator;System;
Keywords=terminal;console;command;prompt;shell;ai;agent;
Terminal=false
StartupNotify=true
StartupWMClass=agentterm
EOF

    info "Created: agentterm.desktop"
}

create_icon_from_svg() {
    info "Creating app icons from SVG..."

    # Use the terminal.svg as base (you should replace with your actual app icon)
    SVG_SOURCE="assets/icons/terminal.svg"

    if [ ! -f "$SVG_SOURCE" ]; then
        error "Source SVG not found: $SVG_SOURCE"
    fi

    check_tool "rsvg-convert" "brew install librsvg (macOS) or apt install librsvg2-bin (Linux)"

    # Create PNG for Linux
    rsvg-convert -w 256 -h 256 "$SVG_SOURCE" > assets/agentterm.png
    info "Created: assets/agentterm.png"

    # Create .icns for macOS
    if [[ "$OSTYPE" == "darwin"* ]]; then
        ICONSET="assets/AppIcon.iconset"
        mkdir -p "$ICONSET"

        for size in 16 32 64 128 256 512; do
            rsvg-convert -w $size -h $size "$SVG_SOURCE" > "$ICONSET/icon_${size}x${size}.png"
            rsvg-convert -w $((size*2)) -h $((size*2)) "$SVG_SOURCE" > "$ICONSET/icon_${size}x${size}@2x.png"
        done

        iconutil -c icns "$ICONSET" -o assets/AppIcon.icns
        rm -rf "$ICONSET"

        info "Created: assets/AppIcon.icns"
    fi
}

create_icon_from_png() {
    info "Creating app icons from PNG..."

    PNG_SOURCE="$1"

    if [ ! -f "$PNG_SOURCE" ]; then
        error "Source PNG not found: $PNG_SOURCE"
    fi

    check_tool "sips" "(macOS built-in)"

    # Copy as Linux icon
    cp "$PNG_SOURCE" assets/agentterm.png
    info "Created: assets/agentterm.png"

    # Create .icns for macOS
    if [[ "$OSTYPE" == "darwin"* ]]; then
        ICONSET="assets/AppIcon.iconset"
        mkdir -p "$ICONSET"

        sips -z 16 16 "$PNG_SOURCE" --out "$ICONSET/icon_16x16.png"
        sips -z 32 32 "$PNG_SOURCE" --out "$ICONSET/icon_16x16@2x.png"
        sips -z 32 32 "$PNG_SOURCE" --out "$ICONSET/icon_32x32.png"
        sips -z 64 64 "$PNG_SOURCE" --out "$ICONSET/icon_32x32@2x.png"
        sips -z 128 128 "$PNG_SOURCE" --out "$ICONSET/icon_128x128.png"
        sips -z 256 256 "$PNG_SOURCE" --out "$ICONSET/icon_128x128@2x.png"
        sips -z 256 256 "$PNG_SOURCE" --out "$ICONSET/icon_256x256.png"
        sips -z 512 512 "$PNG_SOURCE" --out "$ICONSET/icon_256x256@2x.png"
        sips -z 512 512 "$PNG_SOURCE" --out "$ICONSET/icon_512x512.png"
        sips -z 1024 1024 "$PNG_SOURCE" --out "$ICONSET/icon_512x512@2x.png"

        iconutil -c icns "$ICONSET" -o assets/AppIcon.icns
        rm -rf "$ICONSET"

        info "Created: assets/AppIcon.icns"
    fi
}

show_help() {
    echo "Agent Term Packaging Script"
    echo ""
    echo "Usage: $0 <command>"
    echo ""
    echo "Commands:"
    echo "  macos           Build macOS .app bundle"
    echo "  macos-dmg       Build macOS .dmg installer"
    echo "  windows         Build Windows .msi installer"
    echo "  linux-deb       Build Debian/Ubuntu .deb package"
    echo "  linux-rpm       Build Fedora/RHEL .rpm package"
    echo "  linux-appimage  Build Linux AppImage"
    echo "  all-linux       Build all Linux packages"
    echo "  create-desktop  Create .desktop file for Linux"
    echo "  create-icon     Create app icons from terminal.svg"
    echo "  create-icon-png <path>  Create app icons from a PNG file"
    echo "  install-tools   Install all packaging tools"
    echo ""
    echo "Examples:"
    echo "  $0 macos-dmg"
    echo "  $0 create-icon-png ~/Downloads/my-app-icon.png"
    echo "  $0 all-linux"
}

install_tools() {
    info "Installing packaging tools..."

    cargo install cargo-bundle cargo-deb cargo-generate-rpm cargo-wix

    if [[ "$OSTYPE" == "darwin"* ]]; then
        brew install create-dmg librsvg
    fi

    info "All tools installed!"
}

# Main entry point
case "${1:-help}" in
    macos)
        package_macos
        ;;
    macos-dmg)
        package_macos_dmg
        ;;
    windows)
        package_windows
        ;;
    linux-deb)
        package_linux_deb
        ;;
    linux-rpm)
        package_linux_rpm
        ;;
    linux-appimage)
        package_linux_appimage
        ;;
    all-linux)
        package_linux_deb
        package_linux_rpm
        package_linux_appimage
        ;;
    create-desktop)
        create_desktop_file
        ;;
    create-icon)
        create_icon_from_svg
        ;;
    create-icon-png)
        if [ -z "$2" ]; then
            error "Please provide path to PNG file"
        fi
        create_icon_from_png "$2"
        ;;
    install-tools)
        install_tools
        ;;
    help|--help|-h)
        show_help
        ;;
    *)
        error "Unknown command: $1. Run '$0 help' for usage."
        ;;
esac
