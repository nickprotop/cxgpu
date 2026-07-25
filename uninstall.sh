#!/bin/bash
# cxgpu Uninstaller
# Removes cxgpu binary
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

INSTALL_DIR="$HOME/.local/bin"

echo "cxgpu Uninstaller"
echo ""

# Remove binary
if [ -f "$INSTALL_DIR/cxgpu" ]; then
    rm "$INSTALL_DIR/cxgpu"
    echo "✓ Removed $INSTALL_DIR/cxgpu"
else
    echo "  Binary not found at $INSTALL_DIR/cxgpu"
fi

# Remove uninstaller
if [ -f "$INSTALL_DIR/cxgpu-uninstall.sh" ]; then
    rm "$INSTALL_DIR/cxgpu-uninstall.sh"
fi

# Clean PATH from shell config
for RC in "$HOME/.bashrc" "$HOME/.zshrc"; do
    if [ -f "$RC" ] && grep -q "$INSTALL_DIR" "$RC" 2>/dev/null; then
        sed -i "\|$INSTALL_DIR|d" "$RC"
        echo ""
        echo "✓ Removed PATH entry from $RC"
    fi
done

echo ""
echo "✓ cxgpu uninstalled."
