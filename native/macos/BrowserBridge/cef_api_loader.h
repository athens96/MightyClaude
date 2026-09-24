#ifndef MIGHTY_CEF_API_LOADER_H
#define MIGHTY_CEF_API_LOADER_H
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>
#include "include/cef_api_hash.h"

/* A C API client must select the SDK version before calling any CEF function.
 * Without this handshake CEF aborts while wrapping the first callback. */
static int mighty_cef_select_api(void *library) {
    typedef const char *(*hash_fn)(int, int);
    hash_fn hash = (hash_fn)dlsym(library, "cef_api_hash");
    const char *actual = hash ? hash(CEF_API_VERSION, 0) : NULL;
    if (!actual || strcmp(actual, CEF_API_HASH_PLATFORM) != 0) {
        fprintf(stderr, "MightyClaude: CEF SDK/framework API mismatch (version %d)\n", CEF_API_VERSION);
        return 0;
    }
    return 1;
}
#endif
