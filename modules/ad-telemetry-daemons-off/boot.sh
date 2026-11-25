#!/bin/sh
# tvroot module: ad-telemetry-daemons-off
# Masks and stops Samsung's ad/telemetry daemons at every boot.
#
# Why mask --runtime: the units live on the read-only root image with
# Restart=always, and /run/systemd/system is tmpfs, so a plain stop just
# respawns and a persistent mask has nowhere to live. The boot-time module
# runner re-applies this every boot, right after the root chain lands —
# the daemons get ~60-90s of life per power cycle and nothing else.
#
# 1.1.0 — list rebuilt from the live image on QN65S90FAGXZB / T-RSMFUABC-0090
# (2026-11-25). What changed and why:
# - contents-ui-detector-service: After=contents-recognition-service, DB
#   samsung.tv.nvision-analysis-dbmgr. It is the sensor that feeds the
#   recogniser, so masking the recogniser alone left screen analysis running.
# - contents-cognition-service-manager: D-Bus samsung.tizen.CCSMServer, DB
#   samsung.tv.contents-cognition-dbmgr, Restart=always — the supervisor of the
#   contents-analysis stack, i.e. the thing that can re-arm it.
# - COMPANIONS: the trigger units. pisa and both context-aware units are
#   started by .path watchers (active), and pisa is socket-activated as well.
#   Masking a service while its watcher stays live converts a clean disable
#   into activation failures in the boot log.
# - stops are queued with --no-block, the reason documented in
#   samsung-telemetry-off: a blocking stop on a still-activating unit held the
#   boot agent's sdb session for minutes. (1.0.2 dodged the hang by running
#   the stops in parallel, which raced daemon-reload against the mask instead.)
# Neither new service carries a vendor opt-out switch (no negated
# ConditionPathExists in its unit file), so mask is the only lever there.
#
# Disabled state: touch <module dir>/disabled
# Undo (run once as root): systemctl unmask --runtime <unit>; systemctl start <unit>
# Vendor opt-out switches (negated ConditionPathExists in the unit files):
# touching the matching path under /opt/usr/share/systemd/system keeps the
# unit from starting at all — persistent, no mask needed, trivially reverted
# by deleting the file. Masks are still applied as belt-and-braces (they
# also block manual/dbus starts).
OPTOUT=/opt/usr/share/systemd/system
mkdir -p $OPTOUT 2>/dev/null
for o in pisa-service canalysis-daemon slive-provider-daemon \
         com.samsung.tv.context-aware-agent \
         com.samsung.tv.context-aware-service; do
    touch $OPTOUT/$o 2>/dev/null
done

UNITS="
adagent-service
pisa-service
canalysis-service
slive-provider-daemon
contents-recognition-service
contents-ui-detector-service
contents-cognition-service-manager
com.samsung.tv.context-aware-agent
com.samsung.tv.context-aware-service
com.samsung.tv.cis-api-service-app
"
# Activation triggers for the services above, same treatment.
COMPANIONS="
pisa-service.socket
pisa-service.path
com.samsung.tv.context-aware-agent.path
com.samsung.tv.context-aware-service.path
com.samsung.FCCallListener.busname
"
# FCCallListener.busname is slive-provider-daemon's D-Bus activation unit;
# leaving it live turns client calls into failed activations behind the mask.
for u in $UNITS $COMPANIONS; do
    systemctl mask --runtime "$u" >/dev/null 2>&1
    systemctl stop --no-block "$u" >/dev/null 2>&1
done
for u in $UNITS; do
    printf '%s -> %s\n' "$u" "$(systemctl is-active "$u.service" 2>&1)"
done
# Last, not inline: samsung-telemetry-off's sweep SIGKills adagent and pisa,
# which systemd records as failed. Clearing it here keeps --failed meaningful.
for u in $UNITS $COMPANIONS; do
    systemctl reset-failed "$u" >/dev/null 2>&1
done
exit 0
