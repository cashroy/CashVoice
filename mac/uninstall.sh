#!/bin/bash
# Removes CashVoice for macOS. Run: sudo bash uninstall.sh
set -e
osascript -e 'quit app "CashVoice"' 2>/dev/null || true
rm -rf /Applications/CashVoice.app
rm -rf /Library/Audio/Plug-Ins/HAL/CashVoiceDriver.driver
launchctl kickstart -k system/com.apple.audio.coreaudiod 2>/dev/null || killall coreaudiod 2>/dev/null || true
pkgutil --forget com.cashroy.CashVoice 2>/dev/null || true
echo "CashVoice removed. Your settings and sounds are in ~/Library/Application Support/CashVoice (delete it if you like)."
