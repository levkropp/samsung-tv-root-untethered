#!/bin/sh
# tvroot tvplus-off watchdog loop. Every 30 s it re-asserts the autorun
# setting off, removes the boot-into-TV-Plus flag and reaps TV Plus
# processes. Run supervised (tvroot-tvplus.service runs it via cat|bash;
# UEP forbids passing a script file as an argument). While the module is
# disabled the loop sleeps instead of exiting, so systemd never hot-loops it.
# Env: MODULE_DIR, EVIDENCE (set by the unit file).
PATTERNS="tvplusstandalone|bms-service|slive-provider-daemon|slive-util"
POOL=$(ps -ef | grep "launchpad-process-pool" | grep -v grep | tr -s " " | cut -d" " -f2 | head -1)
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
LOG="$EVIDENCE/module-tvplus-off-watchdog.log"
while true; do
    if [ -s "$UI/disabled/tvplus-off" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/tvplus-off" ]; }; then
        sleep 30; continue
    fi
    # db/ keys are persistent (buxton sqlite): write only when it drifted.
    # On the S95F vconftool get prints
    # "db/menu/autorun_tv_plus, value = False (Boolean)"; older builds print 0.
    case "$(vconftool get db/menu/autorun_tv_plus 2>/dev/null)" in
        *"= False"*|*"= false"*|*"= 0"*) ;;
        *) vconftool set -f -t bool db/menu/autorun_tv_plus 0 >/dev/null 2>&1 \
               && echo "$(date '+%T') watchdog re-set autorun_tv_plus=0" >> "$LOG" ;;
    esac
    rm -f /opt/usr/apps/bstr_tvplus_mode /tmp/tvplus_ready 2>/dev/null
    PIDS=$(ps -ef | grep -E "$PATTERNS" | grep -v grep | tr -s " " | cut -d" " -f2)
    for pid in $PIDS; do
        if [ "$pid" != "$$" ] && [ "$pid" != "1" ] && [ "$pid" != "$POOL" ]; then
            cmd=$(tr '\0' ' ' </proc/$pid/cmdline 2>/dev/null | cut -c1-60)
            kill -9 "$pid" >/dev/null 2>&1 && echo "$(date '+%T') watchdog killed $pid $cmd" >> "$LOG"
        fi
    done
    sleep 30
done
