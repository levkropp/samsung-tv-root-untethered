#!/bin/bash
# tvroot tvplus-replace wake listener: opens $APP every time the TV wakes
# from standby, like "Autorun Samsung TV Plus" did at every power-on.
#
# Power-off on these TVs is a background standby, not a shutdown, so the
# cold-boot pin (pin.sh) does not apply. At power-off tvctx-manager
# (set_app_booting_appid_for_background_standby) keeps the current app for
# app booting only if it declares
# http://samsung.com/tv/metadata/appbootingsupport; otherwise it emits
# power.livetv, which /etc/tvctx-manager/appctx-config.json maps to a forced
# launch of the last source, so the TV wakes up in tv-viewer.
#
# deviced broadcasts each display state change on the system bus as
#   org.tizen.system.deviced.displayState.ChangeStateEvent(from, to, ...)
# Captured on an S95F (2026-11-23): power-off "lcdon" -> "standby", wake
# "standby" -> "lcdon", with tv-viewer raised in the same second. The
# listener blocks in dbus-monitor (no CPU while idle) and launches the app
# shortly after each wake, once the TV has finished raising its last source.
#
# Run supervised (tvroot-tvplus-replace-wake.service runs it via cat|bash;
# UEP forbids passing a script file as an argument).
# Env: MODULE_DIR, EVIDENCE, APP.
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
LOG="$EVIDENCE/module-tvplus-replace.log"
MATCH="type='signal',interface='org.tizen.system.deviced.displayState',member='ChangeStateEvent'"
SETTLE=2

[ -n "$APP" ] || exit 0

enabled() {
    ! { [ -s "$UI/disabled/tvplus-replace" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/tvplus-replace" ]; }; }
}

launch() {
    sleep $SETTLE
    enabled || return
    if XDG_RUNTIME_DIR=/run/user/5001 DISPLAY=:0 app_launcher -s "$APP" >/dev/null 2>&1; then
        echo "$(date '+%T') wake: launched $APP" >> "$LOG"
    else
        echo "$(date '+%T') wake: app_launcher $APP failed" >> "$LOG"
    fi
}

# dbus-monitor prints the signal header, then one line per argument:
#   signal time=... interface=...displayState; member=ChangeStateEvent
#      string "standby"
#      string "lcdon"
n=0; from=""
dbus-monitor --system "$MATCH" 2>/dev/null | while IFS= read -r line; do
    case "$line" in
        signal*member=ChangeStateEvent*)
            n=0; from=""
            ;;
        *"string \""*)
            n=$((n + 1))
            v=${line#*\"}; v=${v%\"*}
            if [ "$n" = 1 ]; then
                from=$v
            elif [ "$n" = 2 ] && [ "$from" = "standby" ] && [ "$v" = "lcdon" ]; then
                launch &
            fi
            ;;
    esac
done
# dbus-monitor exited (bus restart): let systemd restart the listener.
exit 1
