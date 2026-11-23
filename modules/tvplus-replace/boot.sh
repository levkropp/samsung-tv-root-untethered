#!/bin/sh
# tvroot module: tvplus-replace
# Gives the two Samsung TV Plus entry points to an app of your choice:
# - power-on: the TV opens the app at every power-on, the way "Autorun
#   Samsung TV Plus" (db/menu/autorun_tv_plus) used to boot into TV Plus;
# - remote: the "Samsung TV Plus" button opens the app.
# Pairs with tvplus-off, which disables TV Plus itself. Nothing forces the
# app back afterwards: you can navigate away freely.
#
# Configuration: /opt/usr/share/selfroot/tvplus-replace.conf (kept outside
# the module dir so module updates do not reset it), one KEY=value per line:
#   APP=znCs95n93Q.OqeeTV   app id to open (required; e.g. OQEE by Free)
#   AUTOSTART=1             open it at power-on (0 to turn off)
#   BUTTON=1                open it on the TV Plus button (0 to turn off)
# Find an app id with: app_launcher -l   (or pkginfo --listpkg)
# Without an APP the module does nothing. Changes apply at the next boot.
#
# Everything runs from one systemd oneshot unit (cat|bash for UEP):
# - pin.sh points the TV's own power-on app booting (app-boot-manager,
#   db/menu/app_booting + db/volt/last_service_app_booting) at the app, so a
#   cold boot starts it natively within seconds. The TV consumes the app id
#   and rewrites db/boot/last_service, so it runs at boot and from ExecStop
#   at shutdown. Verified on an S95F (T-RSMFDEUC-0090), 2026-11-22: OQEE
#   started together with tv-viewer, ~50 s before the modules ran.
# - rule.sh adds a tvctx-manager key rule (the database row an
#   <app-context-rule> manifest entry creates) so the TV launches the app on
#   XF86KeyTVPlus, and removes it at shutdown. Verified on the same S95F,
#   2026-11-23. The TV still shows a brief "Not available" popup: its
#   built-in TV Plus handler fails once tvplus-off has disabled TV Plus.
# - launch.sh is the power-on fallback: once the app framework is up it
#   launches (or brings forward) the app in the owner (uid 5001) session.
# Power-off is usually a background standby, not a shutdown: on wake the TV
# raises its last source (tv-viewer). wake.sh (systemd simple, Restart=always)
# waits in dbus-monitor for deviced's standby -> lcdon ChangeStateEvent and
# opens the app after each wake. Verified on the same S95F, 2026-11-23.
#
# Disabled state: touch <module dir>/disabled or toggle in the manager. The
# runner then skips the module; the key rule is removed at the next shutdown.
MODULE_DIR=${MODULE_DIR:-/opt/usr/share/selfroot/modules/tvplus-replace}
EVIDENCE=${EVIDENCE:-/home/owner/share/tmp/sdk_tools/selfroot-evidence}
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
CONF=/opt/usr/share/selfroot/tvplus-replace.conf
LOG="$EVIDENCE/module-tvplus-replace.log"
RD=/run/systemd/system
UNIT=tvroot-tvplus-replace.service
WAKE_UNIT=tvroot-tvplus-replace-wake.service

if [ -s "$UI/disabled/tvplus-replace" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/tvplus-replace" ]; }; then
    systemctl stop $UNIT $WAKE_UNIT >/dev/null 2>&1
    exit 0
fi

# Read KEY=value lines without sourcing the file.
conf() {
    sed -n "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*\([^[:space:]#]*\).*/\1/p" "$CONF" 2>/dev/null | tail -1
}
APP=$(conf APP)
AUTOSTART=$(conf AUTOSTART)
BUTTON=$(conf BUTTON)
[ "$AUTOSTART" = "0" ] || AUTOSTART=1
[ "$BUTTON" = "0" ] || BUTTON=1

# An app id is a package id plus app name, e.g. znCs95n93Q.OqeeTV. The
# character check also keeps it safe inside the unit file and SQL.
case "$APP" in
    ""|*[!A-Za-z0-9._-]*)
        echo "$(date '+%T') no valid APP in $CONF - nothing to do" >> "$LOG"
        echo "no valid APP in $CONF"
        # stopping the unit removes a key rule left from a previous config
        systemctl stop $UNIT $WAKE_UNIT >/dev/null 2>&1
        exit 0
        ;;
esac

ENV="MODULE_DIR=$MODULE_DIR EVIDENCE=$EVIDENCE APP=$APP AUTOSTART=$AUTOSTART BUTTON=$BUTTON"
# Conflicts/Before shutdown.target: with DefaultDependencies=false the unit
# would otherwise never be stopped, and ExecStop would not run at power-off.
cat > $RD/$UNIT <<UNIT
[Unit]
Description=TVRoot open $APP in place of Samsung TV Plus
DefaultDependencies=false
After=buxton2.socket buxton2.service
Conflicts=shutdown.target
Before=shutdown.target
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/sh -c 'cat $MODULE_DIR/pin.sh | $ENV bash -s boot; cat $MODULE_DIR/rule.sh | $ENV bash -s add; cat $MODULE_DIR/launch.sh | $ENV bash'
ExecStop=/bin/sh -c 'cat $MODULE_DIR/pin.sh | $ENV bash -s shutdown; cat $MODULE_DIR/rule.sh | $ENV bash -s remove'
UNIT

if [ "$AUTOSTART" = "1" ]; then
    cat > $RD/$WAKE_UNIT <<UNIT
[Unit]
Description=TVRoot open $APP when the TV wakes from standby
DefaultDependencies=false
[Service]
Type=simple
ExecStart=/bin/sh -c 'cat $MODULE_DIR/wake.sh | $ENV bash'
Restart=always
RestartSec=5
StartLimitIntervalSec=60
StartLimitBurst=3
UNIT
else
    systemctl stop $WAKE_UNIT >/dev/null 2>&1
    rm -f $RD/$WAKE_UNIT
fi

systemctl daemon-reload 2>/dev/null
for u in $UNIT $WAKE_UNIT; do
    if [ -f "$RD/$u" ]; then
        systemctl restart "$u" 2>/dev/null
        printf '%s -> %s\n' "$u" "$(systemctl is-active "$u" 2>&1)"
    fi
done
exit 0
