// Native bootstrap probe. Managed NuGet/app validation follows after the first CI archive.
#include <TargetConditionals.h>
#if !TARGET_OS_MACCATALYST
#error Expected a Mac Catalyst executable
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <zstd.h>

static void check(size_t result)
{
    if (ZSTD_isError(result)) {
        fprintf(stderr, "Zstandard failed: %s\n", ZSTD_getErrorName(result));
        exit(1);
    }
}

int main(void)
{
    const size_t size = 8 * 1024 * 1024;
    unsigned char *source = malloc(size);
    unsigned char *restored = malloc(size);
    const size_t capacity = ZSTD_compressBound(size);
    void *compressed = malloc(capacity);
    ZSTD_CCtx *context = ZSTD_createCCtx();
    if (!source || !restored || !compressed || !context) return 1;
    for (size_t i = 0; i < size; ++i) source[i] = (unsigned char)(i % 251);
    check(ZSTD_CCtx_setParameter(context, ZSTD_c_nbWorkers, 2));
    check(ZSTD_CCtx_setParameter(context, ZSTD_c_jobSize, 1024 * 1024));
    size_t length = ZSTD_compress2(context, compressed, capacity, source, size);
    check(length);
    size_t restored_size = ZSTD_decompress(restored, size, compressed, length);
    check(restored_size);
    if (restored_size != size || memcmp(source, restored, size) != 0) {
        fprintf(stderr, "Zstandard round trip mismatch\n");
        return 1;
    }
    printf("Zstandard %s: Catalyst multithread round trip passed (%zu bytes, workers=2)\n", ZSTD_versionString(), size);
    ZSTD_freeCCtx(context);
    free(compressed);
    free(restored);
    free(source);
    return 0;
}
