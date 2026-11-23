#!/bin/sh
# tvroot tvplus-replace launcher. Waits for the app framework, then launches
# $APP once, reproducing the old "Autorun Samsung TV Plus" boot-into-app.
# Run supervised (tvroot-tvplus-replace.service runs it via cat|bash; UEP
# forbids passing a script file as an argument). One-shot: it launches and
# exits, so the user can navigate away afterwards.
# Env: MODULE_DIR, EVIDENCE, APP, AUTOSTART (set by the unit file).
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
LOG="$EVIDENCE/module-tvplus-replace.log"

# same enable gate as the runner / boot.sh
if [ -s "$UI/disabled/tvplus-replace" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/tvplus-replace" ]; }; then
    echo "$(date '+%T') disabled - not launching" >> "$LOG"
    exit 0
fi
[ -n "$APP" ] && [ "${AUTOSTART:-1}" = "1" ] || exit 0

# wait (<=90s) for the user session and the app launcher pool to be ready.
i=0
while [ "$i" -lt 45 ]; do
    pool=$(ps -ef | grep "launchpad-process-pool" | grep -v grep | head -1)
    if [ -d /run/user/5001 ] && [ -n "$pool" ]; then break; fi
    sleep 2
    i=$((i + 1))
done
# a short settle so the home UI (csfs) has finished coming up first.
sleep 3

export XDG_RUNTIME_DIR=/run/user/5001
export DISPLAY=:0
if app_launcher -s "$APP" >/dev/null 2>&1; then
    echo "$(date '+%T') launched $APP via app_launcher (waited $((i * 2))s)" >> "$LOG"
else
    r=$(aul_test launch "$APP" 2>&1)
    echo "$(date '+%T') app_launcher failed, aul_test: $r (waited $((i * 2))s)" >> "$LOG"
fi
exit 0
