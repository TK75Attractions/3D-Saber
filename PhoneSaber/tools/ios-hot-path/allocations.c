// この計測用 logger は malloc 内でメモリを確保しない。アプリにはリンクしない。
#include <stdint.h>
#include <stdatomic.h>
#include <sys/resource.h>
extern void (*malloc_logger)(uint32_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uint32_t);
static _Atomic uint64_t allocations;
static void count(uint32_t type, uintptr_t a, uintptr_t b, uintptr_t c, uintptr_t result, uint32_t skip) {
    if ((type & 2) && result) { // MALLOC_LOG_TYPE_ALLOCATE (realloc を含む)
        atomic_fetch_add_explicit(&allocations, 1, memory_order_relaxed);
    }
}
void ps_alloc_start(void) { malloc_logger = count; }
uint64_t ps_alloc_count(void) { return atomic_load_explicit(&allocations, memory_order_relaxed); }
double ps_cpu_seconds(void) {
    struct rusage usage;
    getrusage(RUSAGE_SELF, &usage);
    return usage.ru_utime.tv_sec + usage.ru_utime.tv_usec * 1e-6
        + usage.ru_stime.tv_sec + usage.ru_stime.tv_usec * 1e-6;
}
