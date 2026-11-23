#!/bin/sh
# tvroot tvplus-replace: point the TV's own power-on app booting at $APP.
# app-boot-manager launches db/volt/last_service_app_booting at cold boot
# when db/menu/app_booting is 1, long before the boot chain and modules
# run. The TV clears that app id once used and rewrites db/boot/last_service
# with the last source, so this runs at every boot and again at shutdown.
# Run via cat|bash (UEP). Arg 1: when (boot|shutdown).
# Env: MODULE_DIR, EVIDENCE, APP, AUTOSTART (set by the unit file).
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
LOG="$EVIDENCE/module-tvplus-replace.log"

if [ -s "$UI/disabled/tvplus-replace" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/tvplus-replace" ]; }; then
    exit 0
fi
[ -n "$APP" ] && [ "${AUTOSTART:-1}" = "1" ] || exit 0

vconftool set -f -t int db/menu/app_booting 1 >/dev/null 2>&1
vconftool set -f -t string db/volt/last_service_app_booting "$APP" >/dev/null 2>&1
vconftool set -f -t string db/boot/last_service app-source >/dev/null 2>&1
echo "$(date '+%T') pinned power-on app booting to $APP (${1:-boot})" >> "$LOG" 2>/dev/null
exit 0
