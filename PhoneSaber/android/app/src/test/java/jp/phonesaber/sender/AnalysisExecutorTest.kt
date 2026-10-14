package jp.phonesaber.sender

import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.*
import org.junit.Test

class AnalysisExecutorTest {
    @Test fun deniedPriorityStillRunsAnalysisExpiryAndCleanupOnOneThread() {
        val priorityCalls = AtomicInteger()
        val executor = analysisExecutor {
            priorityCalls.incrementAndGet()
            throw SecurityException("priority denied")
        }
        try {
            // 優先度の拒否でworkerが起動前に死ぬと、解析も停止時のJNI解放も永久に残る。
            val first = executor.submit<Thread> { Thread.currentThread() }
                .get(2, TimeUnit.SECONDS)
            val expiry = executor.schedule<Thread>({ Thread.currentThread() }, 0, TimeUnit.NANOSECONDS)
                .get(2, TimeUnit.SECONDS)
            val cleanup = executor.submit<Thread> { Thread.currentThread() }
                .get(2, TimeUnit.SECONDS)
            assertEquals("PhoneSaber-Analysis", first.name)
            assertSame(first, expiry)
            assertSame(first, cleanup)
            assertEquals(1, priorityCalls.get())
            executor.shutdown()
            assertTrue(executor.awaitTermination(2, TimeUnit.SECONDS))
        } finally {
            executor.shutdownNow()
        }
    }

    @Test fun analysisAndExpiryUseOnePrioritizedThreadBeforeFirstTask() {
        val priorityCalls = AtomicInteger()
        val executor = analysisExecutor { priorityCalls.incrementAndGet() }
        try {
            val first = executor.submit<Pair<Thread, Int>> {
                Thread.currentThread() to priorityCalls.get()
            }.get(2, TimeUnit.SECONDS)
            val expiry = executor.schedule<Thread>({ Thread.currentThread() }, 0, TimeUnit.NANOSECONDS)
                .get(2, TimeUnit.SECONDS)
            assertEquals("PhoneSaber-Analysis", first.first.name)
            assertEquals(1, first.second)
            assertSame(first.first, expiry)
            assertEquals(1, priorityCalls.get())
            val cancelled = executor.schedule({}, 1, TimeUnit.DAYS)
            assertTrue(cancelled.cancel(false))
            assertTrue(executor.queue.isEmpty())
        } finally {
            executor.shutdownNow()
            assertTrue(executor.awaitTermination(2, TimeUnit.SECONDS))
        }
    }
}
