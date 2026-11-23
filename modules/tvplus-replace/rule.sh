#!/bin/sh
# tvroot tvplus-replace: bind the remote's "Samsung TV Plus" button to $APP
# through tvctx-manager's own key rules.
#
# tvctx-manager (libapp-context-manager) reads app key rules from the
# key_filter table of /opt/dbspace/.app-context.db. The package manager
# fills it from <app-context-rule> manifest entries
# (etc/package-manager/parserlib/libapp-context.so) and then changes
# memory/appfw/app_context_db_change; tvctx-manager watches that key and
# reloads the rules (FinalizeKeyHandler, reload_config_from_db,
# InitializeKeyHandler). This adds the row a manifest rule
#   <source selector="key" override="true">XF86KeyTVPlus</source>
#   <target selector="app" request="launch">$APP</target>
# would create, so the TV itself launches the app on the key.
#
# The built-in TV Plus handler (ContextProduct::ProcessTvplus) still runs:
# with tvplus-off its D-Bus call to slive-provider-daemon fails, and it
# emits not_available, which /etc/tvctx-manager/appctx-config.json maps to
# the "Not available" popup. That brief popup cannot be removed from here.
#
# The rule lives only while the module runs: added at boot, removed at
# shutdown (ExecStop), so a disabled module leaves the database clean after
# the next reboot. Our row is the only launch rule on XF86KeyTVPlus (the
# stock one is function-osd "pause").
# Run via cat|bash (UEP). Arg 1: add|remove. Env: MODULE_DIR, EVIDENCE, APP, BUTTON.
UI=/home/owner/share/tmp/sdk_tools/selfroot-ui/modules
LOG="$EVIDENCE/module-tvplus-replace.log"
DB=/opt/dbspace/.app-context.db
# managed_app row added by us (only when the app had none), to undo it.
STATE=/opt/usr/share/selfroot/.tvplus-replace-managed-app
BACKUP=/opt/usr/share/selfroot/.backup-app-context-db
KEY=XF86KeyTVPlus
CHANGE=memory/appfw/app_context_db_change

sql() {
    printf '.timeout 5000\n%s\n' "$1" | sqlite3 "$DB"
}

notify() {
    n=$(vconftool get $CHANGE 2>/dev/null | sed -n 's/.*value = \(-\{0,1\}[0-9][0-9]*\).*/\1/p')
    vconftool set -t int $CHANGE $(( ${n:-0} + 1 )) -f >/dev/null 2>&1
}

# Delete our rule (and our managed_app row). Returns 1 if nothing changed.
remove_rule() {
    rows=$(sql "SELECT count(*) FROM key_filter WHERE key='$KEY' AND request='launch';")
    [ "${rows:-0}" = 0 ] && [ ! -s "$STATE" ] && return 1
    sql "DELETE FROM key_filter WHERE key='$KEY' AND request='launch';"
    if [ -s "$STATE" ]; then
        a=$(head -1 "$STATE" | tr -cd 'A-Za-z0-9._-')
        sql "DELETE FROM managed_app WHERE appid='$a' AND appid NOT IN (SELECT appid FROM key_filter UNION SELECT appid FROM task_filter);"
        rm -f "$STATE"
    fi
    return 0
}

add_rule() {
    if [ ! -f "$BACKUP/.app-context.db" ]; then
        mkdir -p "$BACKUP" && cp -a "$DB" "$BACKUP/" && { [ ! -f "$DB-journal" ] || cp -a "$DB-journal" "$BACKUP/"; }
    fi
    want=$(sql "SELECT count(*) FROM key_filter WHERE key='$KEY' AND request='launch' AND appid='$APP' AND override='true' AND target_selector='package:$APP';")
    rows=$(sql "SELECT count(*) FROM key_filter WHERE key='$KEY' AND request='launch';")
    [ "$want" = 1 ] && [ "$rows" = 1 ] && return 1
    remove_rule
    if [ "$(sql "SELECT count(*) FROM managed_app WHERE appid='$APP';")" = 0 ]; then
        sql "INSERT INTO managed_app (appid, ui_type, source) VALUES ('$APP', NULL, NULL);" && echo "$APP" > "$STATE"
    fi
    sql "INSERT INTO key_filter (appid, key, longpress, override, on_selector, request, target_selector, args) VALUES ('$APP', '$KEY', NULL, 'true', NULL, 'launch', 'package:$APP', NULL);"
}

enabled() {
    ! { [ -s "$UI/disabled/tvplus-replace" ] || { [ -f "$MODULE_DIR/disabled" ] && [ ! -s "$UI/enabled/tvplus-replace" ]; }; }
}

case "$1" in
    add)
        if enabled && [ -n "$APP" ] && [ "${BUTTON:-1}" = "1" ]; then
            add_rule && { notify; echo "$(date '+%T') TV Plus button rule -> $APP" >> "$LOG"; }
        else
            remove_rule && { notify; echo "$(date '+%T') TV Plus button rule removed" >> "$LOG"; }
        fi
        ;;
    remove)
        remove_rule && { notify; echo "$(date '+%T') TV Plus button rule removed" >> "$LOG"; }
        ;;
esac
exit 0
