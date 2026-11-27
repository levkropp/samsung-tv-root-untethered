#!/bin/sh
# tvroot module: samsung-telemetry-off
# Masks Samsung's telemetry/account/update units and kills the ACR service
# app, the Bixby/voice stack, SSO and CIS processes at every boot, then
# supervises two loops via systemd (see below).
#
# WHY SYSTEMD SUPERVISION (learned live 2026-10-10): background shell loops
# spawned from the boot chain die young - orphaned grandchildren of the
# short-lived tarlauncher/sdb shells get reaped (session/cgroup cleanup),
# so PID-file singletons kept vanishing. systemd owns these instead:
# unit files live in /run/systemd/system (tmpfs, rewritten every boot),
# Restart=always with burst limits, and the loops stay dormant (not exit)
# while the module is disabled so supervision never hot-loops.
#
# Two kill classes:
# - systemd units (data-service, pushd, bms, appbinary-manager and, since
#   1.3.0, issue_report_agent, abnormal_user_behavior, media-analytics-service,
#   com.samsung.tv.farfieldvoice, com.samsung.tv.voiceid-service): mask
#   --runtime + stop. Matching .path/.socket watchers stopped too.
# - Tizen apps (acr-service-app, voice-*, bixby-*, ssoservice, CIS):
#   children of launchpad-process-pool; aul relaunches kills within ~20s
#   and de-preloading does not stop it, so voice binaries live renamed
#   and a 30s watchdog reaps the rest. csfs is EXCLUDED (SmartHub home UI
#   framework, demand-launched legitimately - decompiled to confirm).
# - org.tizen.voice-app is neither renamed nor killed (1.2.6):
#   app-context-manager (/etc/tvctx-manager/appctx-config.json, rule
#   voice-app.recovery) relaunches it on every stop. A kill, or a launch
#   failing on the renamed binary, counts as a stop, so it looped ~1
#   launch/s. It is deactivated in pkgmgr instead: amd refuses the launch
#   before forking, so no stop is seen. The binary stays in place so a
#   re-enabled app idles instead of looping.
# NEVER match our own shell: collect PIDs first, then kill excluding $$,
# PID 1 and the launchpad pool. (pkill -f once matched the runner's own
# sh -c cmdline and cut its own transport mid-run.)
#
# 1.3.0 — five more units, all found running and unmasked on QN65S90FAGXZB /
# T-RSMFUABC-0090 (audited 2026-11-25, Description/ExecStart/After read from the
# live unit files):
# - issue_report_agent: /bin/issue_report_agent, Restart=always, protobuf
#   encoded reports. abnormal_user_behavior is After= it and keeps state in
#   /mnt/systemrw/abnormal_user_behavior — the behavioural collector riding the
#   same diagnostics path.
# - media-analytics-service: D-Bus org.tizen.tv.media.analytics.service. It is
#   already "failed" at baseline here; masking it buys silence, not exposure.
# - com.samsung.tv.farfieldvoice ("Start the farfield voice service", started
#   by its own .path) and com.samsung.tv.voiceid-service (voice print, i.e.
#   biometric). Both carry the vendor opt-out switch, so they are opted out
#   *and* masked; the .path is masked too, or the watcher keeps knocking on a
#   masked door.
# - pushd is socket-activated (After=pushd.socket), so the socket is masked
#   along with the service.
# Deliberately NOT touched: app-context-manager (/usr/bin/tvctx-manager
# /etc/tvctx-manager/appctx-config.json) is the app-relaunch rules engine this
# module's own voice-app workaround depends on, and org.tizen.dseal
# (refs ContentsBrowser, mycontents, notification-serviceapp) is a metadata
# helper the content browser calls into. Both are broader blast radius than the
# data they carry.
#
# Note (1.2.3): the old TVPlus/Disney hijack loop is gone. It ran a PS scan
# every 2 s and launched the manager on any "tvplus|deeplink|disney" match,
# which interrupted playback and kept the CPU busy. The manager is opened
# by the user, not by sniffing other apps.
#
# Manager visibility: the agent (SMACK User::Pkg::*) only reads
# User::App::Shared files here; the readable label is re-applied below.
# UEP: interpreters may only run scripts via stdin (cat file | bash),
# never as script-file arguments - the unit files use sh -c 'cat | bash'.
#
# Disabled state: touch <module dir>/disabled (loops go dormant <30s,
# services stopped; revival needs a reboot) - or toggle in the manager.
# Undo units (run once as root): systemctl unmask --runtime <unit>
# Undo voice-app (persistent): pkgcmd -A -t app -n org.tizen.voice-app --uid 5001
#
# Stops are queued with --no-block: the module loop runs inside the boot
# agent's sdb session, and a blocking stop on a unit that is still
# activating (appbinary-manager sat in "deactivating") held that session
# for minutes, so the chain timed out and re-ran and the bridge was
# unusable. The runtime masks already prevent restarts.
MODULE_DIR=${MODULE_DIR:-/opt/usr/share/selfroot/modules/samsung-telemetry-off}
EVIDENCE=${EVIDENCE:-/home/owner/share/tmp/sdk_tools/selfroot-evidence}
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
chsmack -r -a 'User::App::Shared' /opt/usr/share/selfroot/modules 2>/dev/null

# 1.2.3 cleanup: remove the legacy hijack unit on upgrade. /run is tmpfs, so
# this only matters when the module is re-run without a reboot; a reboot
# would have cleared it anyway.
systemctl stop tvroot-hijack.service >/dev/null 2>&1
rm -f /run/systemd/system/tvroot-hijack.service >/dev/null 2>&1
systemctl reset-failed tvroot-hijack.service >/dev/null 2>&1

if [ -s "$UI/disabled/samsung-telemetry-off" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/samsung-telemetry-off" ]; }; then
    systemctl stop tvroot-watchdog.service tvroot-hijack.service >/dev/null 2>&1
    exit 0
fi

# vendor opt-out switches (negated ConditionPathExists - persistent).
OPTOUT=/opt/usr/share/systemd/system
mkdir -p $OPTOUT 2>/dev/null
for o in bms org.tizen.voice-app com.samsung.tv.ondevice-voice \
         com.samsung.tv.farfieldvoice com.samsung.tv.voiceid-service; do
    touch $OPTOUT/$o 2>/dev/null
done
# (Also found, unused: bms honors /run/user/5001/bms_block, creatable
# even without root.)
for u in data-service \
         pushd \
         bms \
         appbinary-manager \
         issue_report_agent \
         abnormal_user_behavior \
         media-analytics-service \
         com.samsung.tv.farfieldvoice \
         com.samsung.tv.voiceid-service; do
    systemctl mask --runtime "$u.service" >/dev/null 2>&1
    systemctl stop --no-block "$u.service" >/dev/null 2>&1
    printf '%s -> %s\n' "$u" "$(systemctl is-active "$u.service" 2>&1)"
done
for p in bms.path \
         com.samsung.tv.ondevice-voice.path \
         org.tizen.voice-app.path \
         slive-provider-daemon.path \
         com.samsung.tv.farfieldvoice.path \
         pushd.socket \
         org.tizen.tv.appbinarymanager.busname \
         org.tizen.tv.media.analytics.service.busname; do
    systemctl mask --runtime "$p" >/dev/null 2>&1
    systemctl stop --no-block "$p" >/dev/null 2>&1
done
# voice-app: deactivate, and undo the rename done by <= 1.2.5 (see header).
pkgcmd -D -t app -n org.tizen.voice-app --uid 5001 >/dev/null 2>&1 && echo "deactivated org.tizen.voice-app"
VA=/opt/usr/apps/org.tizen.voice-app/bin/voice-app
[ -f "$VA.tvroot-disabled" ] && [ ! -e "$VA" ] && mv "$VA.tvroot-disabled" "$VA" 2>/dev/null && echo "restored $VA"
# binary rename floor: voice binaries stay renamed so aul launches fail.
for b in /opt/usr/apps/com.samsung.tv.ondevice-voice/bin/ondevice-voice \
         /opt/usr/apps/org.tizen.voice-client/bin/stt-client \
         /opt/usr/apps/org.tizen.voice-client/bin/voice-client \
         /opt/usr/apps/com.samsung.tv.vif/bin/voice-interaction-framework \
         /opt/usr/apps/com.samsung.tv.bixbycapsuleviewer/bin/bixbycapsuleviewer; do
    [ -f "$b" ] && mv "$b" "$b.tvroot-disabled" 2>/dev/null && echo "renamed $b"
done
PATTERNS="acr-service-app|ondevice-voice|voice-client|voice-interaction|bixbycapsuleviewer|bixby-prov|ssoservice|bms-service|voice-instant-app|voiceassistant-music|mybixby|wakeup-engine-bixby|iacr|cis-api-service-app|farfieldvoice|voiceid-service"
POOL=$(ps -ef | grep "launchpad-process-pool" | grep -v grep | tr -s " " | cut -d" " -f2 | head -1)
sweep() {
    PIDS=$(ps -ef | grep -E "$PATTERNS" | grep -v grep | tr -s " " | cut -d" " -f2)
    for pid in $PIDS; do
        if [ "$pid" != "$$" ] && [ "$pid" != "1" ] && [ "$pid" != "$POOL" ]; then
            cmd=$(tr '\0' ' ' </proc/$pid/cmdline 2>/dev/null | cut -c1-60)
            kill -9 "$pid" >/dev/null 2>&1 && echo "killed $pid $cmd"
        fi
    done
}
sweep
# The sweep SIGKills processes whose units are masked above, and systemd records
# that as a failed unit. Clear it so `systemctl --failed` stays a meaningful
# signal (same ordering lesson as sw-update-off, from the 2026-11-25 dry run).
for u in data-service pushd bms appbinary-manager issue_report_agent \
         abnormal_user_behavior media-analytics-service \
         com.samsung.tv.farfieldvoice com.samsung.tv.voiceid-service; do
    systemctl reset-failed "$u.service" >/dev/null 2>&1
done
# The .path/.socket/.busname companions above can fail on their own before this
# module lands: a cold boot on QN65S90FAGXZB left the farfieldvoice path unit in
# `masked failed failed`, which is exactly the noise that hides a real fault on a
# TV with no journal.
for u in bms.path com.samsung.tv.ondevice-voice.path org.tizen.voice-app.path \
         slive-provider-daemon.path com.samsung.tv.farfieldvoice.path \
         pushd.socket org.tizen.tv.appbinarymanager.busname \
         org.tizen.tv.media.analytics.service.busname; do
    systemctl reset-failed "$u" >/dev/null 2>&1
done
# supervised loop (unit file below runs this via cat|bash for UEP).
WD=/opt/usr/share/selfroot/modules/samsung-telemetry-off/watchdog.sh
RD=/run/systemd/system
cat > $RD/tvroot-watchdog.service <<EOF
[Unit]
Description=TVRoot telemetry watchdog
DefaultDependencies=false
[Service]
Type=simple
ExecStart=/bin/sh -c 'cat $WD | MODULE_DIR=$MODULE_DIR EVIDENCE=$EVIDENCE bash'
Restart=always
RestartSec=5
StartLimitIntervalSec=60
StartLimitBurst=3
EOF
systemctl daemon-reload 2>/dev/null
systemctl restart tvroot-watchdog.service 2>/dev/null
systemctl is-active tvroot-watchdog.service 2>&1
exit 0
