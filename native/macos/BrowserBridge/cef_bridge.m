#include <CoreFoundation/CoreFoundation.h>
#include <crt_externs.h>
#include <libgen.h>
#include <stddef.h>
#include <stdatomic.h>
#include <stdarg.h>
#include <stdlib.h>
#include "include/capi/cef_app_capi.h"
#include "include/capi/cef_browser_capi.h"
#include "include/capi/cef_client_capi.h"
#include "include/capi/cef_life_span_handler_capi.h"
#include "include/capi/cef_request_context_capi.h"
#include "cef_api_loader.h"
#import <objc/runtime.h>
#import "include/cef_application_mac.h"

#define EXPORT __attribute__((visibility("default")))
#define MAX_PANES 32
#define RETAIN(value) do { if (value) { cef_base_ref_counted_t *base = (cef_base_ref_counted_t *)(value); base->add_ref(base); } } while (0)
#define RELEASE(value) do { if (value) { cef_base_ref_counted_t *base = (cef_base_ref_counted_t *)(value); base->release(base); } } while (0)
static void *g_cef;
static char g_frameworks[4096];
static int g_initialized, g_initialize_attempted, g_stopping, g_stopped;

typedef struct pane {
    cef_client_t client;
    cef_life_span_handler_t life;
    atomic_int refs; /* registry ownership plus all CEF interface references */
    void *parent;
    cef_browser_t *browser;
    cef_request_context_t *context;
    int closing;
    int detach_scheduled;
    char *pending_url;
} pane_t;
static pane_t *g_panes[MAX_PANES];
static void trace(const char *format, ...) {
    if (!getenv("MIGHTY_CEF_TRACE")) return;
    va_list args;
    va_start(args, format);
    fprintf(stderr, "MightyCEF: ");
    vfprintf(stderr, format, args);
    fprintf(stderr, "\n");
    va_end(args);
}


static void pane_retain(pane_t *p) { atomic_fetch_add(&p->refs, 1); }
static int pane_release(pane_t *p) {
    if (atomic_fetch_sub(&p->refs, 1) != 1) return 0;
    free(p->pending_url);
    free(p);
    return 1;
}
#define HANDLER_REFS(name, field) \
static pane_t *name##_owner(cef_base_ref_counted_t *b) { return (pane_t *)((char *)b - offsetof(pane_t, field)); } \
static void CEF_CALLBACK name##_add(cef_base_ref_counted_t *b) { pane_retain(name##_owner(b)); } \
static int CEF_CALLBACK name##_release(cef_base_ref_counted_t *b) { return pane_release(name##_owner(b)); } \
static int CEF_CALLBACK name##_one(cef_base_ref_counted_t *b) { return atomic_load(&name##_owner(b)->refs) == 1; } \
static int CEF_CALLBACK name##_any(cef_base_ref_counted_t *b) { return atomic_load(&name##_owner(b)->refs) > 0; }
HANDLER_REFS(client, client)
HANDLER_REFS(life, life)
#define INIT_BASE(p, field, prefix) do { \
(p)->field.base.size = sizeof((p)->field); \
(p)->field.base.add_ref = prefix##_add; \
(p)->field.base.release = prefix##_release; \
(p)->field.base.has_one_ref = prefix##_one; \
(p)->field.base.has_at_least_one_ref = prefix##_any; \
} while (0)

static pane_t *find_pane(void *parent) {
    for (int i = 0; i < MAX_PANES; ++i) if (g_panes[i] && g_panes[i]->parent == parent) return g_panes[i];
    return NULL;
}
static int has_panes(void) {
    for (int i = 0; i < MAX_PANES; ++i) if (g_panes[i]) return 1;
    return 0;
}
static int cef_str(cef_string_t *out, const char *text) {
    typedef int (*convert_fn)(const char *, size_t, cef_string_utf16_t *);
    convert_fn convert = (convert_fn)dlsym(g_cef, "cef_string_utf8_to_utf16");
    return convert && text && convert(text, strlen(text), out);
}
static void clear_str(cef_string_t *s) {
    if (s->str && s->dtor) s->dtor(s->str);
    memset(s, 0, sizeof(*s));
}
static void navigate(pane_t *p, const char *url) {
    cef_frame_t *frame = p->browser->get_main_frame(p->browser);
    if (!frame) return;
    cef_string_t target = {0};
    if (cef_str(&target, url)) frame->load_url(frame, &target);
    clear_str(&target);
    RELEASE(frame);
}
static void close_pane(pane_t *p) {
    trace("close parent=%p browser=%p refs=%d", p->parent, p->browser, atomic_load(&p->refs));
    p->closing = 1;
    if (!p->browser) return; /* on_after_created will close a pending create */
    cef_browser_host_t *host = p->browser->get_host(p->browser);
    if (host) { host->close_browser(host, 1); RELEASE(host); }
}
static void unregister_pane(pane_t *p) {
    trace("unregister parent=%p context=%p refs=%d", p->parent, p->context, atomic_load(&p->refs));
    for (int i = 0; i < MAX_PANES; ++i) if (g_panes[i] == p) g_panes[i] = NULL;
    RELEASE(p->context);
    p->context = NULL;
    pane_release(p);
}
static void CEF_CALLBACK after_created(cef_life_span_handler_t *self, cef_browser_t *browser) {
    pane_t *p = life_owner(&self->base);
    /* CEF transfers one reference for callback object arguments. Keep this
     * reference until on_before_close instead of taking an extra reference. */
    p->browser = browser;
    trace("after_created parent=%p browser=%p refs=%d closing=%d", p->parent, browser, atomic_load(&p->refs), p->closing);
    if (p->closing || g_stopping) close_pane(p);
    else if (p->pending_url) {
        navigate(p, p->pending_url);
        free(p->pending_url);
        p->pending_url = NULL;
    }
}
static int CEF_CALLBACK do_close(cef_life_span_handler_t *self, cef_browser_t *browser) {
    pane_t *p = life_owner(&self->base);
    p->closing = 1;
    trace("do_close parent=%p browser=%p refs=%d", p->parent, browser, atomic_load(&p->refs));
    if (p->detach_scheduled) { RELEASE(browser); return 1; }
    cef_browser_host_t *host = browser->get_host(browser);
    if (!host) { RELEASE(browser); return 1; }
    NSView *view = (NSView *)host->get_window_handle(host);
    RELEASE(host);
    if (view) {
        p->detach_scheduled = 1;
        /* Default CEF close targets the top-level NSWindow, which belongs to
         * SwiftUI and contains other panes. Tear down just CEF's child view
         * after this callback unwinds. CefBrowserHostView.dealloc notifies CEF
         * that its native host was destroyed and triggers on_before_close. */
        trace("schedule detach view=%p parent=%p", view, p->parent);
        /* Do not capture an ObjC strong reference in the copied CF block:
         * disposal of that block can wait for the outer application's pool,
         * keeping the native view alive throughout a synchronous close drain. */
        void *retained_view = [view retain];
        CFRunLoopPerformBlock(CFRunLoopGetMain(), kCFRunLoopCommonModes, ^{
            @autoreleasepool {
                NSView *child = (NSView *)retained_view;
                trace("detach view=%p superview=%p", child, [child superview]);
                [child removeFromSuperview];
                [child release];
            }
        });
        CFRunLoopWakeUp(CFRunLoopGetMain());
    }
    RELEASE(browser);
    return 1;
}
static void CEF_CALLBACK before_close(cef_life_span_handler_t *self, cef_browser_t *browser) {
    pane_t *p = life_owner(&self->base);
    trace("before_close parent=%p stored=%p callback=%p refs=%d", p->parent, p->browser, browser, atomic_load(&p->refs));
    RELEASE(p->browser);
    p->browser = NULL;
    RELEASE(browser); /* this callback's separate transferred reference */
    unregister_pane(p);
}
/* A pane owns one browser. Keep user-initiated target=_blank links in that
 * pane instead of letting an unmanaged popup reuse and overwrite its client. */
static int CEF_CALLBACK before_popup(
    cef_life_span_handler_t *self, cef_browser_t *browser, cef_frame_t *frame,
    int popup_id, const cef_string_t *target_url, const cef_string_t *target_frame_name,
    cef_window_open_disposition_t disposition, int user_gesture,
    const cef_popup_features_t *features, cef_window_info_t *window,
    cef_client_t **client, cef_browser_settings_t *settings,
    cef_dictionary_value_t **extra, int *no_javascript_access) {
    (void)frame; (void)popup_id; (void)target_frame_name; (void)disposition;
    (void)features; (void)window; (void)client; (void)settings; (void)extra;
    (void)no_javascript_access;
    pane_t *p = life_owner(&self->base);
    if (user_gesture && !p->closing && !g_stopping && target_url && target_url->length) {
        cef_frame_t *main = browser->get_main_frame(browser);
        if (main) { main->load_url(main, target_url); RELEASE(main); }
    }
    /* The popup is canceled, so consume all transferred object parameters and
     * return no replacement client or extra_info to the caller. */
    RELEASE(browser);
    RELEASE(frame);
    if (client) { RELEASE(*client); *client = NULL; }
    if (extra) { RELEASE(*extra); *extra = NULL; }
    return 1;
}
static cef_life_span_handler_t *CEF_CALLBACK get_life(cef_client_t *self) {
    pane_t *p = client_owner(&self->base);
    pane_retain(p); /* returning a CEF interface transfers one reference */
    return &p->life;
}

EXPORT int mighty_cef_load(const char *path) {
    if (g_cef) return 1;
    if (!path || g_stopped) return 0;
    void *library = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (!library) { fprintf(stderr, "MightyClaude: cannot load CEF: %s\n", dlerror()); return 0; }
    if (!mighty_cef_select_api(library)) { dlclose(library); return 0; }
    g_cef = library; /* process lifetime; never unload live Chromium code */
    char copy[4096];
    snprintf(copy, sizeof(copy), "%s", path);
    snprintf(g_frameworks, sizeof(g_frameworks), "%s", dirname(dirname(copy)));
    return 1;
}
EXPORT void *mighty_cef_initialize_sym(void) { return g_cef ? dlsym(g_cef, "cef_initialize") : NULL; }
EXPORT void *mighty_cef_create_browser_sym(void) { return g_cef ? dlsym(g_cef, "cef_browser_host_create_browser") : NULL; }
EXPORT int mighty_cef_is_initialized(void) { return g_initialized; }
EXPORT int mighty_cef_is_browser_closed(void *parent) { return !find_pane(parent); }
EXPORT int mighty_cef_has_browser(void *parent) { pane_t *p = find_pane(parent); return p && p->browser && !p->closing; }
EXPORT int mighty_cef_is_loading(void *parent) {
    pane_t *p = find_pane(parent);
    return p && (!p->browser || p->browser->is_loading(p->browser));
}

static int ensure_initialized(const char *root) {
    if (g_initialized) return !g_stopping;
    if (!g_cef || g_initialize_attempted || g_stopped || g_stopping) return 0;
    typedef int (*initialize_fn)(const cef_main_args_t *, const cef_settings_t *, cef_app_t *, void *);
    initialize_fn initialize = (initialize_fn)dlsym(g_cef, "cef_initialize");
    if (!initialize) return 0;
    /* Swift owns NSApplication; register the SDK protocol only after verifying
     * that its custom application implements both required methods. */
    if (![NSApp respondsToSelector:@selector(isHandlingSendEvent)] ||
        ![NSApp respondsToSelector:@selector(setHandlingSendEvent:)]) {
        fprintf(stderr, "MightyClaude: NSApplication does not implement CEF event handling\n");
        return 0;
    }
    class_addProtocol([NSApp class], @protocol(CefAppProtocol));
    cef_settings_t settings = {0};
    settings.size = sizeof(settings);
    settings.no_sandbox = 1;
    settings.log_severity = LOGSEVERITY_ERROR;
    char path[4608];
    snprintf(path, sizeof(path), "%s/Chromium Embedded Framework.framework", g_frameworks);
    cef_str(&settings.framework_dir_path, path);
    snprintf(path, sizeof(path), "%s/Chromium Embedded Framework.framework/Resources", g_frameworks);
    cef_str(&settings.resources_dir_path, path);
    cef_str(&settings.locales_dir_path, path);
    snprintf(path, sizeof(path), "%s/MightyClaude Helper.app/Contents/MacOS/MightyClaude Helper", g_frameworks);
    cef_str(&settings.browser_subprocess_path, path);
    cef_str(&settings.root_cache_path, root);
    cef_main_args_t args = { *_NSGetArgc(), *_NSGetArgv() };
    /* CEF forbids retrying initialization after a false return, including
     * singleton early exit. Latch only the actual call so missing prerequisites
     * above do not consume the process's one initialization attempt. */
    g_initialize_attempted = 1;
    trace("initialize root=%s", root);
    int success = initialize(&args, &settings, NULL, NULL);
    trace("initialize result=%d", success);
    clear_str(&settings.framework_dir_path);
    clear_str(&settings.resources_dir_path);
    clear_str(&settings.locales_dir_path);
    clear_str(&settings.browser_subprocess_path);
    clear_str(&settings.root_cache_path);
    g_initialized = success;
    if (!success) {
        fprintf(stderr, "MightyClaude: CEF initialization failed; browser disabled until app restart\n");
    }
    return success;
}
/* Must be called before NSApplication's event loop starts. Installing CEF's
 * run-loop observers mid-loop misses the current entry and underflows their
 * nesting stack on the next CFRunLoopExit. */
EXPORT int mighty_cef_initialize(const char *root_cache) {
    return root_cache && root_cache[0] && ensure_initialized(root_cache);
}
EXPORT void mighty_cef_work(void) {
    if (!g_initialized) return;
    void (*work)(void) = (void (*)(void))dlsym(g_cef, "cef_do_message_loop_work");
    if (work) { @autoreleasepool { work(); } }
}
EXPORT void mighty_cef_close(void *parent) {
    pane_t *p = find_pane(parent);
    if (p && !p->closing) close_pane(p);
}
EXPORT void mighty_cef_shutdown(void) {
    if (!g_initialized || g_stopped) return;
    g_stopping = 1;
    for (int i = 0; i < MAX_PANES; ++i) if (g_panes[i] && !g_panes[i]->closing) close_pane(g_panes[i]);
    /* Chromium close is asynchronous. Keep its pump and AppKit run loop alive
     * until on_before_close releases every browser; never shutdown underneath
     * live browser objects, even if a subprocess is unresponsive. */
    CFAbsoluteTime deadline = CFAbsoluteTimeGetCurrent() + 5.0;
    while (has_panes() && CFAbsoluteTimeGetCurrent() < deadline) {
        @autoreleasepool {
            mighty_cef_work();
            CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.005, false);
        }
    }
    if (has_panes()) {
        fprintf(stderr, "MightyClaude: CEF close timed out; leaving engine loaded until process exit\n");
        return;
    }
    void (*shutdown)(void) = (void (*)(void))dlsym(g_cef, "cef_shutdown");
    if (shutdown) shutdown();
    g_initialized = 0;
    g_stopped = 1; /* CEF cannot initialize again in this process */
}
EXPORT int mighty_cef_show(void *parent, int width, int height, const char *cache, const char *url) {
    if (!parent || !cache || !url || !g_initialized || g_stopping || g_stopped) return 0;
    pane_t *existing = find_pane(parent);
    if (existing) {
        if (existing->closing) return 0;
        if (existing->browser) navigate(existing, url);
        else { free(existing->pending_url); existing->pending_url = strdup(url); }
        return 1;
    }
    int slot = 0;
    while (slot < MAX_PANES && g_panes[slot]) ++slot;
    if (slot == MAX_PANES) return 0;
    typedef cef_request_context_t *(*context_fn)(const cef_request_context_settings_t *, cef_request_context_handler_t *);
    typedef int (*create_fn)(const cef_window_info_t *, cef_client_t *, const cef_string_t *, const cef_browser_settings_t *, cef_dictionary_value_t *, cef_request_context_t *);
    context_fn create_context = (context_fn)dlsym(g_cef, "cef_request_context_create_context");
    create_fn create = (create_fn)dlsym(g_cef, "cef_browser_host_create_browser");
    if (!create_context || !create) return 0;
    pane_t *p = calloc(1, sizeof(*p));
    if (!p) return 0;
    atomic_init(&p->refs, 1);
    p->parent = parent;
    INIT_BASE(p, client, client);
    INIT_BASE(p, life, life);
    p->client.get_life_span_handler = get_life;
    p->life.on_before_popup = before_popup;
    p->life.on_after_created = after_created;
    p->life.do_close = do_close;
    p->life.on_before_close = before_close;
    cef_request_context_settings_t rc = {0};
    rc.size = sizeof(rc);
    rc.persist_session_cookies = 1;
    cef_str(&rc.cache_path, cache);
    p->context = create_context(&rc, NULL);
    clear_str(&rc.cache_path);
    if (!p->context) { pane_release(p); return 0; }
    cef_window_info_t window = {0};
    window.size = sizeof(window);
    window.parent_view = (cef_window_handle_t)parent;
    window.bounds.width = width > 0 ? width : 1;
    window.bounds.height = height > 0 ? height : 1;
    cef_browser_settings_t settings = {0};
    settings.size = sizeof(settings);
    cef_string_t target = {0};
    cef_str(&target, url);
    g_panes[slot] = p;
    /* CEF consumes one reference for each object crossing the C API boundary
     * (except the self receiver). Retain the registry's context/client while
     * transferring independent references to create_browser. */
    RETAIN(&p->client);
    RETAIN(p->context);
    trace("create parent=%p context=%p refs=%d cache=%s", parent, p->context, atomic_load(&p->refs), cache);
    int success = create(&window, &p->client, &target, &settings, NULL, p->context);
    trace("create result=%d parent=%p refs=%d", success, parent, atomic_load(&p->refs));
    clear_str(&target);
    if (!success) unregister_pane(p);
    return success;
}
