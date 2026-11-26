#!/bin/sh
# tvroot module: sshd-service
# Replaces the sdb/relay management channel with an SSH listener that survives boot. The relay
# costs a full untether per command and wedges sdbd until a MAINS POWER CYCLE whenever a command
# outruns the CLI window (eight incidents), which no amount of care inside a command prevents.
#
# The server is managed IL under the vendor-signed /usr/bin/dotnet host: docs/PLAN-manager.md
# records that installed IL is not signature-checked (SELP/UEP gate native ELF), so this needs no
# firmware write and disables no security control.
#
# systemd owns the process for the same reason the bridge does: a process started from inside a
# root window keeps that session's descriptors, and Restart=always covers a crash we cannot
# predict from a probe. systemctl is safe ONLY here -- the boot chain runs as root in the vendor
# context, while the exploit agent's SMACK 'User' context is what wedged sdbd (M98).
MODULE_DIR=${MODULE_DIR:-/opt/usr/share/selfroot/modules/sshd-service}
EVIDENCE=${EVIDENCE:-/home/owner/share/tmp/sdk_tools/selfroot-evidence}
K=/opt/usr/share/selfroot/sshd
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
PORT=${TVROOT_SSHD_PORT:-22}

log() { printf '%s sshd-service: %s\n' "$(date)" "$1"; }

if [ -s "$UI/disabled/sshd-service" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/sshd-service" ]; }; then
    systemctl stop tvroot-sshd.service >/dev/null 2>&1
    log "disabled; stopped"
    exit 0
fi

# Payload must be staged next to this script (the manager pushes the module directory).
missing=""
for f in TvRootSshd.dll Microsoft.DevTunnels.Ssh.dll Microsoft.DevTunnels.Ssh.Keys.dll TvRootSshd.runtimeconfig.json; do
    [ -s "$MODULE_DIR/$f" ] || missing="$missing $f"
done
if [ -n "$missing" ]; then
    log "FATAL: payload not staged:$missing"
    exit 0
fi

mkdir -p "$K" 2>/dev/null
mkdir -p "$K/hostkeys" 2>/dev/null
# The admin key is copied once and never overwritten: an operator editing the live file must not
# have their change reverted by the next boot, and a boot must never start a listener nobody can
# authenticate to. If neither exists, skip rather than generate a key we cannot deliver.
if [ ! -s "$K/authorized_keys" ]; then
    if [ -s "$MODULE_DIR/authorized_keys" ]; then
        cp "$MODULE_DIR/authorized_keys" "$K/authorized_keys" 2>/dev/null && log "installed authorized_keys"
    else
        log "FATAL: no authorized_keys in $K or $MODULE_DIR; not starting"
        exit 0
    fi
fi

RD=/run/systemd/system
cat > $RD/tvroot-sshd.service <<EOF
[Unit]
Description=TVRoot management sshd (managed IL, publickey only)
DefaultDependencies=false
[Service]
Type=simple
WorkingDirectory=$MODULE_DIR
ExecStart=/usr/bin/dotnet $MODULE_DIR/TvRootSshd.dll --port $PORT --host-key $K/hostkeys/ssh_host_rsa_key --authorized-keys $K/authorized_keys --log $K/sshd.log
Restart=always
RestartSec=5
StartLimitIntervalSec=60
StartLimitBurst=3
EOF
if [ ! -s $RD/tvroot-sshd.service ]; then
    log "FATAL: could not write unit"
    exit 0
fi
systemctl daemon-reload 2>/dev/null
systemctl restart tvroot-sshd.service 2>/dev/null
log "unit written port=$PORT state=$(systemctl is-active tvroot-sshd.service 2>&1)"
exit 0
