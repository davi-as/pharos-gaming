#!/bin/bash
# Works around a BlueZ LE reconnection bug: bluetoothd's own auto-reconnect
# attempt for the (already paired/trusted) Xbox controller often fails
# instantly with "No route to host" if it races the controller's advertising
# window. Manually retrying bluetoothctl connect a few seconds later succeeds
# because it catches the next advertisement. This loop just automates that
# manual retry so the controller connects on its own when powered on.

. ~/.config/console/config.env
MAC=$XBOX_MAC

while true; do
    if ! bluetoothctl info "$MAC" 2>/dev/null | grep -q "Connected: yes"; then
        timeout 8 bluetoothctl connect "$MAC" >/dev/null 2>&1
    fi
    sleep 4
done
