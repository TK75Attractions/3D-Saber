// 外部 profiler が sandbox で拒否される場合の macOS/arm64 用 PC sampling。
// 計測用 executable だけにリンクし、production core には組み込まない。
#include <csignal>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <dlfcn.h>
#include <sys/time.h>
#include <sys/ucontext.h>
#include <mach/arm/thread_status.h>

#define main benchmark_main
#include "benchmark.cpp"
#undef main

namespace {
constexpr int capacity = 1000000;
static_assert(std::atomic<uintptr_t>::is_always_lock_free);
std::atomic<uintptr_t> pcs[capacity];
volatile sig_atomic_t count = 0;
void sample_pc(int, siginfo_t*, void* context) {
    int index = count;
    if (index < capacity) {
        auto* state = static_cast<ucontext_t*>(context);
        pcs[index].store(arm_thread_state64_get_pc(state->uc_mcontext->__ss),std::memory_order_relaxed);
        count = index+1;
    }
}
}
int main(int argc, char** argv) {
    struct sigaction action{};
    action.sa_sigaction = sample_pc;
    action.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGALRM,&action,nullptr) != 0) return 2;
    itimerval timer{{0,1000},{0,1000}};
    if (setitimer(ITIMER_REAL,&timer,nullptr) != 0) return 2;
    int result = benchmark_main(argc,argv);
    timer = {};
    setitimer(ITIMER_REAL,&timer,nullptr);
    sigset_t blocked;
    sigemptyset(&blocked); sigaddset(&blocked,SIGALRM);
    sigprocmask(SIG_BLOCK,&blocked,nullptr);
    Dl_info info{};
    dladdr(reinterpret_cast<void*>(&benchmark_main),&info);
    // atos -o <executable> -l <base> <PC...> で関数・行に変換する。
    std::fprintf(stderr,"sampling_base=%p samples=%d\n",info.dli_fbase,int(count));
    for (int i = 0; i < count; ++i)
        std::fprintf(stderr,"%#lx\n",static_cast<unsigned long>(pcs[i].load(std::memory_order_relaxed)));
    return result;
}
