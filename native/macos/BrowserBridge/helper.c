#include <mach-o/dyld.h>
#include <stdint.h>
#include "include/capi/cef_app_capi.h"
#include "cef_api_loader.h"

static int strip_component(char *path) {
    char *slash = strrchr(path, '/');
    if (!slash) return 0;
    *slash = '\0';
    return 1;
}

int main(int argc, char **argv) {
    char path[4096], framework[4608];
    uint32_t size = sizeof(path);
    if (_NSGetExecutablePath(path, &size) != 0) return 1;
    /* .../Frameworks/Helper.app/Contents/MacOS/Helper */
    for (int i = 0; i < 4; ++i) if (!strip_component(path)) return 1;
    snprintf(framework, sizeof(framework), "%s/Chromium Embedded Framework.framework/Chromium Embedded Framework", path);
    void *library = dlopen(framework, RTLD_NOW | RTLD_GLOBAL);
    if (!library) {
        fprintf(stderr, "MightyClaude helper: cannot load CEF: %s\n", dlerror());
        return 1;
    }
    if (!mighty_cef_select_api(library)) return 1;
    typedef int (*execute_fn)(const cef_main_args_t *, cef_app_t *, void *);
    execute_fn execute = (execute_fn)dlsym(library, "cef_execute_process");
    if (!execute) {
        fprintf(stderr, "MightyClaude helper: missing cef_execute_process\n");
        return 1;
    }
    cef_main_args_t args = { argc, argv };
    int result = execute(&args, NULL, NULL);
    return result < 0 ? 1 : result;
}
