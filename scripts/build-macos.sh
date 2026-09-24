#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Respect an explicit DEVELOPER_DIR, otherwise use xcode-select (including CI's selected Xcode).
# Never replace the selected toolchain with a hard-coded Command Line Tools path.
SWIFT_EXECUTABLE="$(xcrun --find swift)"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-release}"
BUILD_SCRATCH="${MIGHTY_BUILD_SCRATCH:-$PROJECT_ROOT/native/macos/.build}"
MODULE_CACHE="$BUILD_SCRATCH/module-cache"
APP_PATH="${MIGHTY_MACOS_APP_PATH:-$PROJECT_ROOT/release/native-macos/MightyClaude.app}"
mkdir -p "$MODULE_CACHE"
export CLANG_MODULE_CACHE_PATH="$MODULE_CACHE"
export SWIFTPM_MODULECACHE_OVERRIDE="$MODULE_CACHE"
bash "$PROJECT_ROOT/scripts/package-icons.sh"

"$SWIFT_EXECUTABLE" build --disable-sandbox --package-path "$PROJECT_ROOT/native/macos" --scratch-path "$BUILD_SCRATCH" -c "$BUILD_CONFIGURATION" -Xswiftc -module-cache-path -Xswiftc "$MODULE_CACHE"
BIN_PATH="$("$SWIFT_EXECUTABLE" build --disable-sandbox --package-path "$PROJECT_ROOT/native/macos" --scratch-path "$BUILD_SCRATCH" -c "$BUILD_CONFIGURATION" --show-bin-path)"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources/mods"
cp "$BIN_PATH/MightyClaude" "$APP_PATH/Contents/MacOS/MightyClaude"
# `ditto` merges rather than replaces, so a Styles/ left by an earlier build
# would satisfy the gate below even after the manifests stopped being packaged.
STYLE_BUNDLE="$APP_PATH/Contents/Resources/MightyClaude_MightyCore.bundle"
rm -rf "$STYLE_BUNDLE/Contents/Resources/Styles" "$STYLE_BUNDLE/Styles"
# SwiftPM resource bundles contain Ghostty's terminfo and shell integration.
# They must travel with the app; a development checkout is not a runtime dependency.
for resource_bundle in "$BIN_PATH"/*.bundle; do
  if [ -d "$resource_bundle" ]; then
    ditto "$resource_bundle" "$APP_PATH/Contents/Resources/$(basename "$resource_bundle")"
  fi
done
# The bundled style manifests must travel inside that bundle. Losing them is
# silent at runtime — every pane just falls back to the plain CLI — so the
# second gate of docs/mighty-styles.md §3.2 is here, at assembly.
# `-s` rather than `-f`: a zero-byte ouroboros.json is exactly the failure this
# gate exists to catch, and it passes an existence check.
for style_name in ouroboros paperthin; do
  if [ ! -s "$STYLE_BUNDLE/Contents/Resources/Styles/$style_name.json" ] && [ ! -s "$STYLE_BUNDLE/Styles/$style_name.json" ]; then
    echo "번들 스타일 매니페스트가 없습니다: $style_name.json ($STYLE_BUNDLE)" >&2
    exit 1
  fi
done
ditto "$PROJECT_ROOT/native/licenses" "$APP_PATH/Contents/Resources/ThirdPartyLicenses"
cp "$PROJECT_ROOT/assets/icons/MightyClaude.icns" "$APP_PATH/Contents/Resources/MightyClaude.icns"
cp "$PROJECT_ROOT/assets/icons/mightyclaude.png" "$APP_PATH/Contents/Resources/mightyclaude.png"
if [ -d "$PROJECT_ROOT/assets/pets" ]; then
  ditto "$PROJECT_ROOT/assets/pets" "$APP_PATH/Contents/Resources/pets"
fi
ditto "$PROJECT_ROOT/mods/mighty-bridge" "$APP_PATH/Contents/Resources/mods/mighty-bridge"
cat > "$APP_PATH/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>MightyClaude</string>
<key>CFBundleDisplayName</key><string>MightyClaude</string>
<key>CFBundleIdentifier</key><string>dev.mightyclaude.native</string>
<key>CFBundleExecutable</key><string>MightyClaude</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>MightyClaude.icns</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSPrincipalClass</key><string>MightyApplication</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/><key>NSAllowsArbitraryLoads</key><true/></dict>
</dict></plist>
PLIST
# Version and update address: VERSION file (or MIGHTY_APP_VERSION), commit count
# (or MIGHTY_BUILD_NUMBER), and the manifest URL the app checks (MIGHTY_UPDATE_URL).
APP_VERSION="${MIGHTY_APP_VERSION:-$(tr -d '[:space:]' < "$PROJECT_ROOT/VERSION" 2>/dev/null || echo 0.1.0)}"
BUILD_NUMBER="${MIGHTY_BUILD_NUMBER:-$(git -C "$PROJECT_ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"
plutil -replace CFBundleShortVersionString -string "$APP_VERSION" "$APP_PATH/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$BUILD_NUMBER" "$APP_PATH/Contents/Info.plist"
if [ -n "${MIGHTY_UPDATE_URL:-}" ]; then
  plutil -replace MightyUpdateManifestURL -string "$MIGHTY_UPDATE_URL" "$APP_PATH/Contents/Info.plist"
fi
# Base64 raw Ed25519 public key; with it the app accepts only signed manifests.
if [ -n "${MIGHTY_UPDATE_PUBLIC_KEY:-}" ]; then
  plutil -replace MightyUpdatePublicKey -string "$MIGHTY_UPDATE_PUBLIC_KEY" "$APP_PATH/Contents/Info.plist"
fi
# Browser engine bundling: CEF framework, helper stubs, pinned Node, licence notices.
# Requires a warm engine cache (run scripts/fetch-browser-engine.sh first).
if [ "${MIGHTY_BROWSER_ENGINE:-}" = "1" ]; then
    LOCK_FILE="$PROJECT_ROOT/native/macos/BrowserEngine.lock"
    CEF_URL="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['cef']['url'])" "$LOCK_FILE")"
    NODE_URL="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['node']['url'])" "$LOCK_FILE")"
    NODE_VERSION="$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(d['node']['version'])" "$LOCK_FILE")"
    LOCK_HASH="$(shasum -a 256 "$LOCK_FILE" | awk '{print $1}')"
    ENGINE_CACHE="${MIGHTY_BROWSER_ENGINE_CACHE:-$HOME/Library/Caches/MightyClaude/browser-engine/${LOCK_HASH:0:16}}"
    CEF_ARCHIVE="$ENGINE_CACHE/$(basename "$CEF_URL")"
    NODE_ARCHIVE="$ENGINE_CACHE/$(basename "$NODE_URL")"
    if [ ! -f "$CEF_ARCHIVE" ] || [ ! -f "$NODE_ARCHIVE" ]; then
        echo "browser engine not in cache — run scripts/fetch-browser-engine.sh first" >&2
        exit 1
    fi
    EXTRACT_TEMP="$(mktemp -d)"
    # shellcheck disable=SC2064
    trap "rm -rf '$EXTRACT_TEMP'" EXIT
    FW_DIR="$APP_PATH/Contents/Frameworks"
    mkdir -p "$FW_DIR"
    # CEF framework
    CEF_TOP="$(tar -tjf "$CEF_ARCHIVE" | head -1 | cut -d/ -f1)"
    tar -xjf "$CEF_ARCHIVE" -C "$EXTRACT_TEMP" 2>/dev/null
    ditto "$EXTRACT_TEMP/$CEF_TOP/Release/Chromium Embedded Framework.framework" \
          "$FW_DIR/Chromium Embedded Framework.framework"
    # Licence notices (CEF and Chromium names satisfy the check)
    LIC_DIR="$APP_PATH/Contents/Resources/ThirdPartyLicenses"
    cp "$EXTRACT_TEMP/$CEF_TOP/LICENSE.txt"  "$LIC_DIR/CEF-LICENSE.txt"
    cp "$EXTRACT_TEMP/$CEF_TOP/CREDITS.html" "$LIC_DIR/Chromium-CREDITS.html"
    # Node runtime (only the node binary is required by the check)
    NODE_BASE="node-v${NODE_VERSION}-darwin-arm64"
    tar -xzf "$NODE_ARCHIVE" -C "$EXTRACT_TEMP" "$NODE_BASE/bin/node" 2>/dev/null
    NODE_DST="$APP_PATH/Contents/Resources/browser/node/bin"
    mkdir -p "$NODE_DST"
    cp "$EXTRACT_TEMP/$NODE_BASE/bin/node" "$NODE_DST/node"
    chmod +x "$NODE_DST/node"
    # Real CEF helpers: call cef_execute_process via dlopen/dlsym so the Swift
    # package builds without CEF headers.  Each helper resolves the framework
    # relative to its own executable path at runtime.
    HELPER_C="$EXTRACT_TEMP/helper.c"
    cat > "$HELPER_C" <<'CSRC'
#include <dlfcn.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>

typedef struct { int argc; char** argv; } cef_main_args_t;
typedef int (*cef_execute_process_fn)(const cef_main_args_t*, void*, void*);

static void strip_last_component(char* buf) {
    char* s = strrchr(buf, '/');
    if (s) *s = '\0';
}

int main(int argc, char* argv[]) {
    char buf[4096];
    uint32_t sz = (uint32_t)sizeof(buf);
    if (_NSGetExecutablePath(buf, &sz) != 0) return 1;
    /* Helper exe is at: .../Contents/Frameworks/Name.app/Contents/MacOS/Name */
    /* Strip: executable name, MacOS, Contents, Name.app */
    /* Result:            .../Contents/Frameworks */
    strip_last_component(buf);
    strip_last_component(buf);
    strip_last_component(buf);
    strip_last_component(buf);
    char fw[4096];
    snprintf(fw, sizeof(fw),
        "%s/Chromium Embedded Framework.framework/Chromium Embedded Framework",
        buf);
    void* cef = dlopen(fw, RTLD_NOW | RTLD_GLOBAL);
    if (!cef) return 0;
    cef_execute_process_fn fn =
        (cef_execute_process_fn)dlsym(cef, "cef_execute_process");
    if (!fn) return 0;
    cef_main_args_t args = { argc, argv };
    return fn(&args, NULL, NULL);
}
CSRC
    xcrun clang -o "$EXTRACT_TEMP/helper_bin" -arch arm64 -mmacosx-version-min=14.0 "$HELPER_C"

    # CEF engine bridge dylib: the app target loads this at runtime so that
    # cef_initialize and cef_browser_host_create_browser are reachable without
    # linking the Swift package against CEF headers at build time.  The CEF C
    # API headers come from the extracted, pinned SDK, and every CEF entry
    # point is still resolved with dlsym, so the dylib has no link-time
    # dependency on the framework.
    BRIDGE_C="$EXTRACT_TEMP/cef_bridge.c"
    cat > "$BRIDGE_C" <<'CSRC'
#include <dlfcn.h>
#include <libgen.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "include/capi/cef_app_capi.h"
#include "include/capi/cef_browser_capi.h"
#include "include/capi/cef_client_capi.h"
#include "include/capi/cef_life_span_handler_capi.h"
#include "include/capi/cef_request_context_capi.h"

static const char k_cef_initialize[]                  = "cef_initialize";
static const char k_cef_browser_host_create_browser[] = "cef_browser_host_create_browser";

static void* g_cef = NULL;
static char  g_frameworks[4096] = {0};   /* .../Contents/Frameworks          */
static char  g_framework[4096]  = {0};   /* .../Chromium Embedded Framework  */
static int   g_initialized = 0;

/* CEF entry points, resolved lazily from the framework. */
typedef int  (*fn_initialize_t)(const cef_main_args_t*, const cef_settings_t*,
                                cef_app_t*, void*);
typedef int  (*fn_create_browser_t)(const cef_window_info_t*, cef_client_t*,
                                    const cef_string_t*, const cef_browser_settings_t*,
                                    cef_dictionary_value_t*, cef_request_context_t*);
typedef cef_request_context_t* (*fn_create_context_t)(
    const cef_request_context_settings_t*, cef_request_context_handler_t*);
typedef int  (*fn_utf8_to_utf16_t)(const char*, size_t, cef_string_utf16_t*);
typedef void (*fn_do_work_t)(void);
typedef void (*fn_shutdown_t)(void);

__attribute__((visibility("default")))
int mighty_cef_load(const char* fw_path) {
    if (g_cef) return 1;
    g_cef = dlopen(fw_path, RTLD_NOW | RTLD_GLOBAL);
    if (!g_cef) return 0;
    snprintf(g_framework, sizeof(g_framework), "%s", fw_path);
    /* fw_path is <Frameworks>/Chromium Embedded Framework.framework/Chromium
     * Embedded Framework: two levels up is the app's Frameworks directory. */
    char copy[4096];
    snprintf(copy, sizeof(copy), "%s", fw_path);
    snprintf(g_frameworks, sizeof(g_frameworks), "%s", dirname(dirname(copy)));
    return 1;
}

__attribute__((visibility("default")))
void* mighty_cef_initialize_sym(void) {
    return g_cef ? dlsym(g_cef, k_cef_initialize) : NULL;
}

__attribute__((visibility("default")))
void* mighty_cef_create_browser_sym(void) {
    return g_cef ? dlsym(g_cef, k_cef_browser_host_create_browser) : NULL;
}

/* ── strings ───────────────────────────────────────────────────────────── */

static int cef_str(cef_string_t* out, const char* utf8) {
    fn_utf8_to_utf16_t f = (fn_utf8_to_utf16_t)dlsym(g_cef, "cef_string_utf8_to_utf16");
    if (!f || !utf8) return 0;
    return f(utf8, strlen(utf8), out);
}

/* ── ref-counting ──────────────────────────────────────────────────────── */

/* The handlers below are process-lifetime globals, so reference counting is a
 * no-op: nothing CEF holds on to can outlive the app process. */
static void stub_add_ref(struct _cef_base_ref_counted_t* b)              { (void)b; }
static int  stub_release(struct _cef_base_ref_counted_t* b)              { (void)b; return 0; }
static int  stub_has_one_ref(struct _cef_base_ref_counted_t* b)          { (void)b; return 1; }
static int  stub_has_at_least_one_ref(struct _cef_base_ref_counted_t* b) { (void)b; return 1; }

#define BASE_INIT(ptr) do { \
    (ptr)->base.size                 = sizeof(*(ptr)); \
    (ptr)->base.add_ref              = stub_add_ref; \
    (ptr)->base.release              = stub_release; \
    (ptr)->base.has_one_ref          = stub_has_one_ref; \
    (ptr)->base.has_at_least_one_ref = stub_has_at_least_one_ref; \
} while (0)

/* ── one browser per pane, keyed by the pane's NSView ──────────────────── */

#define MAX_PANES 32
typedef struct {
    void*            parent_view;
    cef_browser_t*   browser;
    cef_request_context_t* context;
} pane_t;

static pane_t g_panes[MAX_PANES];
static int    g_pane_count = 0;
static void*  g_pending_parent = NULL;

static pane_t* pane_for(void* parent_view) {
    for (int i = 0; i < g_pane_count; i++) {
        if (g_panes[i].parent_view == parent_view) return &g_panes[i];
    }
    return NULL;
}

static cef_app_t            g_app;
static cef_client_t         g_client;
static cef_life_span_handler_t g_life_span;

static void on_after_created(struct _cef_life_span_handler_t* self,
                             cef_browser_t* browser) {
    (void)self;
    pane_t* p = pane_for(g_pending_parent);
    if (p && !p->browser) p->browser = browser;
}

static cef_life_span_handler_t* client_get_life_span(struct _cef_client_t* s) {
    (void)s;
    return &g_life_span;
}

static void init_handlers(void) {
    memset(&g_app, 0, sizeof(g_app));
    memset(&g_client, 0, sizeof(g_client));
    memset(&g_life_span, 0, sizeof(g_life_span));
    BASE_INIT(&g_app);
    BASE_INIT(&g_client);
    BASE_INIT(&g_life_span);
    g_life_span.on_after_created   = on_after_created;
    g_client.get_life_span_handler = client_get_life_span;
}

/* ── engine lifecycle ──────────────────────────────────────────────────── */

/* Starts CEF once per app process, lazily, the first time a pane needs it.
 * |root_cache| is the browser-profiles root that every workspace profile lives
 * under; the helper that runs cef_execute_process sits next to this dylib. */
static int ensure_initialized(const char* root_cache) {
    if (g_initialized) return 1;
    if (!g_cef) return 0;
    fn_initialize_t initialize = (fn_initialize_t)dlsym(g_cef, k_cef_initialize);
    if (!initialize) return 0;

    init_handlers();

    cef_settings_t settings;
    memset(&settings, 0, sizeof(settings));
    settings.size = sizeof(settings);
    settings.no_sandbox = 1;
    settings.log_severity = LOGSEVERITY_ERROR;

    char buf[4608];
    snprintf(buf, sizeof(buf), "%s/Chromium Embedded Framework.framework", g_frameworks);
    cef_str(&settings.framework_dir_path, buf);
    snprintf(buf, sizeof(buf), "%s/Chromium Embedded Framework.framework/Resources", g_frameworks);
    cef_str(&settings.resources_dir_path, buf);
    cef_str(&settings.locales_dir_path, buf);
    snprintf(buf, sizeof(buf),
             "%s/MightyClaude Helper.app/Contents/MacOS/MightyClaude Helper", g_frameworks);
    cef_str(&settings.browser_subprocess_path, buf);
    cef_str(&settings.root_cache_path, root_cache);

    cef_main_args_t args;
    memset(&args, 0, sizeof(args));
    if (!initialize(&args, &settings, &g_app, NULL)) return 0;
    g_initialized = 1;
    return 1;
}

/* Pumped from the app's main run loop so SwiftUI is never blocked. */
__attribute__((visibility("default")))
void mighty_cef_work(void) {
    if (!g_initialized) return;
    fn_do_work_t work = (fn_do_work_t)dlsym(g_cef, "cef_do_message_loop_work");
    if (work) work();
}

__attribute__((visibility("default")))
void mighty_cef_shutdown(void) {
    if (!g_initialized) return;
    fn_shutdown_t shutdown = (fn_shutdown_t)dlsym(g_cef, "cef_shutdown");
    if (shutdown) shutdown();
    g_initialized = 0;
}

/* ── the pane's single entry point ─────────────────────────────────────── */

/* Shows |url| inside |parent_view|. The first call for a view starts the
 * engine if needed, creates this workspace's request context on |cache_path|
 * and a windowed browser parented to the view; later calls navigate the
 * browser that is already there. Returns 1 when the page was handed to CEF. */
__attribute__((visibility("default")))
int mighty_cef_show(void* parent_view, int width, int height,
                    const char* cache_path, const char* url) {
    if (!parent_view || !cache_path || !url) return 0;

    char root[4096];
    snprintf(root, sizeof(root), "%s", cache_path);
    char* parent_dir = dirname(root);
    if (!ensure_initialized(parent_dir)) return 0;

    pane_t* pane = pane_for(parent_view);
    if (pane && pane->browser) {
        cef_frame_t* frame = pane->browser->get_main_frame(pane->browser);
        if (!frame) return 0;
        cef_string_t target = {0};
        cef_str(&target, url);
        frame->load_url(frame, &target);
        return 1;
    }
    if (g_pane_count >= MAX_PANES) return 0;

    /* Each workspace gets its own request context so its cookies and logins
     * live in its own profile directory under the root cache. */
    fn_create_context_t create_context =
        (fn_create_context_t)dlsym(g_cef, "cef_request_context_create_context");
    if (!create_context) return 0;
    cef_request_context_settings_t rc;
    memset(&rc, 0, sizeof(rc));
    rc.size = sizeof(rc);
    rc.persist_session_cookies = 1;
    cef_str(&rc.cache_path, cache_path);
    cef_request_context_t* context = create_context(&rc, NULL);
    if (!context) return 0;

    fn_create_browser_t create_browser =
        (fn_create_browser_t)dlsym(g_cef, k_cef_browser_host_create_browser);
    if (!create_browser) return 0;

    cef_window_info_t wi;
    memset(&wi, 0, sizeof(wi));
    wi.size = sizeof(wi);
    wi.parent_view = (cef_window_handle_t)parent_view;
    wi.bounds.x = 0;
    wi.bounds.y = 0;
    wi.bounds.width = width > 0 ? width : 1;
    wi.bounds.height = height > 0 ? height : 1;

    cef_browser_settings_t bs;
    memset(&bs, 0, sizeof(bs));
    bs.size = sizeof(bs);

    cef_string_t target = {0};
    cef_str(&target, url);

    pane_t* slot = &g_panes[g_pane_count++];
    slot->parent_view = parent_view;
    slot->browser = NULL;
    slot->context = context;
    g_pending_parent = parent_view;

    int ok = create_browser(&wi, &g_client, &target, &bs, NULL, context);
    if (!ok) g_pane_count--;
    return ok;
}

/* Closes the browser hosted in |parent_view| when its tab goes away. */
__attribute__((visibility("default")))
void mighty_cef_close(void* parent_view) {
    pane_t* pane = pane_for(parent_view);
    if (!pane || !pane->browser) return;
    cef_browser_host_t* host = pane->browser->get_host(pane->browser);
    if (host) host->close_browser(host, 1);
    pane->browser = NULL;
    pane->parent_view = NULL;
}
CSRC
    xcrun clang -dynamiclib -o "$APP_PATH/Contents/Frameworks/MightyCEFBridge.dylib" \
        -arch arm64 -mmacosx-version-min=14.0 \
        -I "$EXTRACT_TEMP/$CEF_TOP" \
        -install_name "@rpath/MightyCEFBridge.dylib" \
        "$BRIDGE_C"

    make_helper() {
        local name="$1" bundle_id="$2"
        local happ="$FW_DIR/${name}.app"
        mkdir -p "$happ/Contents/MacOS"
        cp "$EXTRACT_TEMP/helper_bin" "$happ/Contents/MacOS/$name"
        cat > "$happ/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$bundle_id</string>
<key>CFBundleExecutable</key><string>$name</string>
<key>CFBundleName</key><string>$name</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
</dict></plist>
PLIST
    }
    make_helper "MightyClaude Helper"           "dev.mightyclaude.native.helper"
    make_helper "MightyClaude Helper (GPU)"      "dev.mightyclaude.native.helper.gpu"
    make_helper "MightyClaude Helper (Renderer)" "dev.mightyclaude.native.helper.renderer"
    make_helper "MightyClaude Helper (Plugin)"   "dev.mightyclaude.native.helper.plugin"
    rm -rf "$EXTRACT_TEMP"
    trap - EXIT
fi
# Ad-hoc signatures differ per build, so the Keychain treats every rebuild as a
# new app and asks again for the remote connection key. A stable local
# code-signing certificate (Keychain Access → Certificate Assistant, type
# "Code Signing") makes "Always Allow" stick across rebuilds.
CODESIGN_IDENTITY="${MIGHTY_CODESIGN_IDENTITY:--}"
# Sign CEF inner dylibs explicitly (not reached by --deep on the main bundle)
if [ "${MIGHTY_BROWSER_ENGINE:-}" = "1" ]; then
    CEF_FW="$APP_PATH/Contents/Frameworks/Chromium Embedded Framework.framework"
    for dylib in "$CEF_FW/Libraries/"*.dylib; do
        [ -f "$dylib" ] && codesign --force --sign "$CODESIGN_IDENTITY" "$dylib"
    done
    for helper_app in "$APP_PATH/Contents/Frameworks/"*.app; do
        [ -d "$helper_app" ] && codesign --force --sign "$CODESIGN_IDENTITY" "$helper_app"
    done
    [ -f "$APP_PATH/Contents/Frameworks/MightyCEFBridge.dylib" ] && \
        codesign --force --sign "$CODESIGN_IDENTITY" \
            "$APP_PATH/Contents/Frameworks/MightyCEFBridge.dylib"
    codesign --force --sign "$CODESIGN_IDENTITY" "$CEF_FW"
fi
codesign --force --deep --sign "$CODESIGN_IDENTITY" "$APP_PATH"
# Building must not change LaunchServices registrations while the installed app
# may be running. install-macos.sh handles registration after the app has quit.
printf '%s\n' "$APP_PATH"
