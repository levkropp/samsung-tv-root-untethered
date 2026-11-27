#!/bin/sh
# tvroot module: sw-update-off
# Kills Samsung's update machinery at every boot: the firmware OTA stack, the
# firmware download step, the lightweight-web-engine image swap and app-store
# background updates. Owner policy: no updates, ever - an OTA would wipe the
# root (DB rows, renamed binaries, preload), and this TV runs two sideloaded
# web apps and nothing a Samsung update could improve.
#
# 1.1.0: the unit list was rebuilt from the live image on QN65S90FAGXZB /
# T-RSMFUABC-0090 (2026-11-25), which found two problems with 1.0.0:
# - fw-usb-update-service{.service,.path} do not exist on this build; both
#   mask/stop calls were silent no-ops.
# - swu-auto-upgrade.timer is armed (OnActiveSec=120) and nothing masked it,
#   so the check still fired at boot+120s; and the core/service/verifier
#   daemons, the verifier timer, the download step and the LWE image mount were
#   not covered at all.
#
# Layers (all re-applied every boot; nothing here touches ro partitions):
# - vendor opt-out: /mnt/backup/OS_UPGRADE_FLAG. Grepping the unit files shows
#   it is the negated ConditionPathExists gate on five units -
#   swu-auto-upgrade, swu-core-tv, swu-service, swu-verifier-tv,
#   bootmode-swu-standalone - so it is the master switch for the automatic
#   stack, not a hint. It is a persistent file on the backup volume
#   (/mnt/backup -> /mnt/systemrw, rw vdfs); delete it and reboot to revert.
#   This is also why 1.0.0's "USB update path" wording was wrong: the flag
#   gates the automatic check, not the manual USB install.
# - runtime mask + stop, including every .timer/.path companion. A masked
#   service with a live trigger becomes a failed job on every later boot, and
#   a log full of expected failures hides real ones. Stops are queued with
#   --no-block for the reason documented in samsung-telemetry-off (a blocking
#   stop on a still-activating unit held the boot agent's sdb session for
#   minutes; appbinary-manager and fw-update-service both sit in deactivating).
# - kill sweep for stragglers: PIDs collected first, then skip $$, PID 1 and
#   the launchpad pool.
#
# NOT masked, deliberately - the way back in. These are the manual reflash and
# factory paths; all of them need user action or USB media, and they are
# starved rather than masked because nothing downloads any more:
#   swu-standalone.service/.target, bootmode-swu-standalone.service,
#   os-upgrade.target (wants system-rw-update.service, the User=root
#   /usr/share/upgrade/update.sh), swu-factory-usb-upgrade.target,
#   ses-usb-installer@.service. Do not add them to UNITS.
#
# Cost, stated plainly: no security patches for the firmware, for the
# preinstalled apps, or for the lightweight web engine the sideloaded apps run
# on. To patch once: rm /mnt/backup/OS_UPGRADE_FLAG, unmask, reboot.
#
# Disabled state: touch <module dir>/disabled
# Undo (run once as root): rm /mnt/backup/OS_UPGRADE_FLAG;
#   systemctl unmask --runtime <unit>; systemctl start <unit>
UNITS="
swu-auto-upgrade.service
swu-auto-upgrade.path
swu-auto-upgrade.timer
swu-core-tv.service
swu-service.service
swu-verifier-tv.service
swu-verifier-tv.timer
fw-update-service.service
fw-update-service.path
fw-update-init-download.service
lightweight-web-engine-update.service
was-background-update.service
was-background-update.path
"
# D-Bus activation units for the daemons above. Masking only the service leaves
# the busname unit holding the well-known name, so the first client call turns
# into a failed activation job — observed live here as
# org.tizen.tv.appbinarymanager.busname after masking appbinary-manager.service.
BUSNAMES="
org.tizen.tv.swu.core.TV.busname
org.tizen.tv.swu.otascheduler.otascheduler.busname
org.tizen.tv.swu.service.servicemanager.busname
org.tizen.tv.swu.verifier.TV.busname
org.tizen.tv.fwupdateservice.busname
org.tizen.tv.initdownload.busname
"
touch /mnt/backup/OS_UPGRADE_FLAG 2>/dev/null
for u in $UNITS $BUSNAMES; do
    systemctl mask --runtime "$u" >/dev/null 2>&1
    systemctl stop --no-block "$u" >/dev/null 2>&1
done
for u in swu-auto-upgrade swu-core-tv swu-service swu-verifier-tv \
         fw-update-service fw-update-init-download was-background-update; do
    printf '%s -> %s\n' "$u" "$(systemctl is-active "$u.service" 2>&1 | head -1)"
done
printf 'OS_UPGRADE_FLAG -> %s\n' "$(ls /mnt/backup/OS_UPGRADE_FLAG 2>&1 | head -1)"
POOL=$(ps -ef | grep "launchpad-process-pool" | grep -v grep | tr -s " " | cut -d" " -f2 | head -1)
PIDS=$(ps -ef | grep -E "SWUAutoUpgrade|SWUCoreTV|SWUService|SWUVerifierTV|fw-update-service|fw-update-init-download|was_background_update" | grep -v grep | tr -s " " | cut -d" " -f2)
for pid in $PIDS; do
    if [ "$pid" != "$$" ] && [ "$pid" != "1" ] && [ "$pid" != "$POOL" ]; then
        cmd=$(tr '\0' ' ' < /proc/$pid/cmdline 2>/dev/null | cut -c1-60)
        kill -9 "$pid" >/dev/null 2>&1 && echo "killed $pid $cmd"
    fi
done
# reset-failed runs last, on purpose: the sweep SIGKILLs stragglers and systemd
# records that as a failed unit (adagent-service and pisa-service did exactly
# this in the 2026-11-25 dry run). Clearing it here keeps `systemctl --failed`
# a useful signal instead of burying real faults in expected noise.
for u in $UNITS $BUSNAMES; do
    systemctl reset-failed "$u" >/dev/null 2>&1
done
exit 0
