package jp.phonesaber.sender

import android.content.Context
import android.content.ComponentName
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import androidx.camera.view.PreviewView
import androidx.lifecycle.LifecycleOwner
import java.util.concurrent.ScheduledThreadPoolExecutor
import org.junit.Assert.*
import org.junit.Test
import org.mockito.Mockito.*

class CameraSessionTest {
    private fun withFailedProvider(test: (CameraSession, MutableList<Runnable>, MutableList<String>,
                                         PackageManager) -> Unit) {
        val context = mock(Context::class.java)
        val preview = mock(PreviewView::class.java)
        `when`(preview.context).thenReturn(context)
        val sender = mock(LatestUdpSender::class.java)
        `when`(sender.healthMeter).thenReturn(DeviceHealthMeter())
        `when`(context.applicationContext).thenReturn(context)
        val packages = mock(PackageManager::class.java)
        `when`(context.packageManager).thenReturn(packages)
        `when`(packages.getServiceInfo(any(ComponentName::class.java), anyInt()))
            .thenThrow(RuntimeException("getServiceInfo failed"))
        val posts = mutableListOf<Runnable>()
        val failures = mutableListOf<String>()
        mockStatic(Looper::class.java).use {
            mockConstruction(Handler::class.java) { handler, _ ->
                doAnswer { call -> posts.add(call.getArgument(0)); true }
                    .`when`(handler).post(any(Runnable::class.java))
            }.use {
                // JNI・カメラ・画面を起動せず、開始と失敗通知の世代管理を実行する。
                mockConstruction(ScheduledThreadPoolExecutor::class.java).use {
                    mockConstruction(ComponentName::class.java).use {
                        val camera = CameraSession(mock(LifecycleOwner::class.java), preview,
                            sender, failures::add)
                        try { test(camera, posts, failures, packages) }
                        finally { camera.close() }
                    }
                }
            }
        }
    }

    @Test fun synchronousProviderFailureIsReportedAndAllowsRestart() = withFailedProvider { camera, posts, failures, packages ->
        camera.start(145, 25, MirrorSettings())
        assertTrue(failures.isEmpty())
        posts.single().run()
        assertEquals(listOf("カメラ開始失敗: getServiceInfo failed"), failures)
        camera.stop()
        camera.start(145, 25, MirrorSettings())
        posts.last().run()
        assertEquals(2, failures.size)
        verify(packages, times(2)).getServiceInfo(any(ComponentName::class.java), anyInt())
    }

    @Test fun stoppedOrSupersededProviderFailureDoesNotRecoverANewSession() {
        for (restart in listOf(false, true)) withFailedProvider { camera, posts, failures, packages ->
            camera.start(145, 25, MirrorSettings())
            val stale = posts.single()
            camera.stop()
            if (restart) camera.start(145, 25, MirrorSettings())
            stale.run()
            assertTrue("停止前の失敗通知で復旧を始めない", failures.isEmpty())
            verify(packages, times(if (restart) 2 else 1))
                .getServiceInfo(any(ComponentName::class.java), anyInt())
        }
    }
}
