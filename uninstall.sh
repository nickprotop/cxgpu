#!/bin/bash
# cxnvmon Uninstaller
# Removes cxnvmon binary
# Copyright (c) Nikolaos Protopapas. All rights reserved.
# Licensed under the MIT License.

INSTALL_DIR="$HOME/.local/bin"

echo "cxnvmon Uninstaller"
echo ""

# Remove binary
if [ -f "$INSTALL_DIR/cxnvmon" ]; then
    rm "$INSTALL_DIR/cxnvmon"
    echo "✓ Removed $INSTALL_DIR/cxnvmon"
else
    echo "  Binary not found at $INSTALL_DIR/cxnvmon"
fi

# Remove uninstaller
if [ -f "$INSTALL_DIR/cxnvmon-uninstall.sh" ]; then
    rm "$INSTALL_DIR/cxnvmon-uninstall.sh"
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
echo "✓ cxnvmon uninstalled."
