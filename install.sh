#!/bin/bash
# cxgpu Installer
# Downloads and installs the latest release from GitHub
# Usage: curl -fsSL https://raw.githubusercontent.com/nickprotop/cxgpu/main/install.sh | bash
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

set -e

REPO="nickprotop/cxgpu"
INSTALL_DIR="$HOME/.local/bin"

echo "Installing cxgpu..."

# Detect OS and architecture
OS=$(uname -s)
ARCH=$(uname -m)

case "$OS" in
    Linux)
        case "$ARCH" in
            x86_64)  BINARY="cxgpu-linux-x64" ;;
            aarch64) BINARY="cxgpu-linux-arm64" ;;
            *) echo "Error: Unsupported Linux architecture: $ARCH"; exit 1 ;;
        esac
        ;;
    Darwin)
        # No macOS build: there is no modern NVIDIA driver for macOS, and the AMD backend needs
        # Linux sysfs or the ROCm CLI. A binary that installs and then reports no GPUs would be
        # worse than declining here.
        echo "Error: cxgpu does not support macOS."
        echo "It needs nvidia-smi, or Linux sysfs/ROCm for AMD — none are available on macOS."
        exit 1
        ;;
    *)
        echo "Error: Unsupported OS: $OS"
        echo "cxgpu supports Linux and Windows. For Windows, download from GitHub Releases."
        exit 1
        ;;
esac

# Get latest release info
echo "Fetching latest release..."
RELEASE_INFO=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")
TAG=$(echo "$RELEASE_INFO" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": "\(.*\)".*/\1/')
VERSION="${TAG#v}"

if [ -z "$TAG" ]; then
    echo "Error: Could not determine latest release."
    exit 1
fi

echo "Latest version: $VERSION"

# Download binary
DOWNLOAD_URL="https://github.com/$REPO/releases/download/$TAG/$BINARY"
echo "Downloading $BINARY..."

mkdir -p "$INSTALL_DIR"
curl -fsSL "$DOWNLOAD_URL" -o "$INSTALL_DIR/cxgpu"
chmod +x "$INSTALL_DIR/cxgpu"

# Download uninstaller
curl -fsSL "https://raw.githubusercontent.com/$REPO/main/uninstall.sh" -o "$INSTALL_DIR/cxgpu-uninstall.sh"
chmod +x "$INSTALL_DIR/cxgpu-uninstall.sh"

# Ensure PATH
if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
    SHELL_RC=""
    if [ -f "$HOME/.zshrc" ]; then
        SHELL_RC="$HOME/.zshrc"
    elif [ -f "$HOME/.bashrc" ]; then
        SHELL_RC="$HOME/.bashrc"
    fi

    if [ -n "$SHELL_RC" ]; then
        if ! grep -q "$INSTALL_DIR" "$SHELL_RC" 2>/dev/null; then
            echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$SHELL_RC"
            echo "Added $INSTALL_DIR to PATH in $SHELL_RC"
        fi
    fi
fi

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  ✓ cxgpu v$VERSION installed!"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "  Binary:  $INSTALL_DIR/cxgpu"
echo ""
echo "  Run:     cxgpu"
echo "  Remove:  cxgpu-uninstall.sh"
echo ""
if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
    echo "  Note: Restart your shell or run:"
    echo "    source ~/.bashrc  (or ~/.zshrc)"
fi
