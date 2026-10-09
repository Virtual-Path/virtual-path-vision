using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using VirtualPathVision.Components;

namespace VirtualPathVision.Tests
{
    /// <summary>
    /// 关窗死锁的回归测试。
    ///
    /// <para><b>原始 bug</b>：<c>VideoCaptureComponent.StopCapture()</c> 在等待采集循环
    /// 结束之后<b>还要再取一次 <c>_lock</c></b>。而采集线程退出时的 finally 会
    /// <c>lock (_lock) { SetState(Disconnected); }</c>，<c>SetState</c> 同步触发
    /// <c>OnConnectionStateChanged</c>，主窗口的处理器用
    /// <c>Dispatcher.Invoke</c> —— 采集线程于是<b>持有 _lock 的同时</b>等 UI 线程泵消息。</para>
    ///
    /// <para>两条腿凑齐就是死锁：UI 线程等 <c>_lock</c>，采集线程等 Dispatcher，
    /// 而 Dispatcher 只有 UI 线程能泵。关窗永久挂死
    /// （端到端 harness 上实测 5 次里挂 1~3 次）。</para>
    ///
    /// <para><b>为什么不按"跑多次看会不会挂"来测</b>：这是调度竞态，谁先拿到
    /// <c>_lock</c> 取决于两侧到达锁点的先后。要在无人值守的测试里稳定命中，
    /// 必须把这条竞态的<b>充分条件</b>直接造出来，而不是碰运气。
    /// 实测"压力跑 N 轮"的写法命中率不足：判据造出了全部前提
    /// （等待超时、状态事件被卡），仍然测不出死锁——因为 UI 线程总会先抢到锁。</para>
    ///
    /// <para><b>本测试的判据（确定性）</b>：<c>StopCapture()</c> 在完成开头那次取锁、
    /// 进入等待之后，<b>绝不应该再需要 <c>_lock</c></b>。用例在它等待期间从外部
    /// 按住 <c>_lock</c> 三秒；若 <c>StopCapture</c> 仍在 2.2 秒内返回，
    /// 就证明它没有二次取锁；一旦取锁，它必然被按住到三秒之后。</para>
    /// </summary>
    public static class ShutdownDeadlockTests
    {
        /// <summary>组件内部等待采集循环的上限（VideoCaptureComponent 里是 1s）。</summary>
        private const int StopWaitMs = 1000;

        /// <summary>外部按住 _lock 的时长。</summary>
        private const int HoldLockMs = 3000;

        /// <summary>StopCapture 必须在此之内返回（需明显小于 HoldLockMs 才有区分度）。</summary>
        private const int AllowedMs = 2200;

        public static void StopCapture_DoesNotRetakeLockAfterWait(Action<string, bool> check)
            => StopCaptureCore(check);

        public static void Dispose_IsIdempotent(Action<string, bool> check)
            => DisposeCore(check);

        private static void StopCaptureCore(Action<string, bool> check)
        {
            const string Name = "shutdown: StopCapture does not retake _lock after the wait";

            string video = MakeVideo();
            var cap = new VideoCaptureComponent();

            var frameEntered = new ManualResetEventSlim(false);
            var releaseFrame = new ManualResetEventSlim(false);

            // 把采集循环钉在帧处理里，使 StopCapture 的 1 秒等待<b>必然超时</b>。
            // 这一步必需：循环若能及时退出，等待会立刻返回，
            // StopCapture 也就还没走到"等待之后"那段代码，测不到任何东西。
            int seen = 0;
            cap.OnFrameCaptured += (_, __) =>
            {
                if (Interlocked.Increment(ref seen) > 1) return;
                frameEntered.Set();
                releaseFrame.Wait();
            };

            cap.SourceType = VideoSourceType.FileReplay;
            cap.ReplayPath = video;
            cap.ReplayLoop = true;
            cap.ReplayFps = 60;

            bool started = cap.StartCapture();
            bool running = frameEntered.Wait(5000);
            object? lockObj = started && running ? GetPrivateLockObject(cap) : null;

            if (lockObj == null)
            {
                check(Name + $" [前置失败: started={started}, running={running}]", false);
                try { cap.Dispose(); } catch { }
                return;
            }

            var stopper = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                cap.StopCapture();
                sw.Stop();
                return sw.ElapsedMilliseconds;
            });

            // 只等 StopCapture 走完开头那次取锁，然后立刻按住 _lock。
            // 必须在它的等待<b>期间</b>按住：否则等它超时返回、走到
            // "等待之后"那段代码时，锁早已放开，就什么都测不到了。
            Thread.Sleep(250);

            bool entered = Monitor.TryEnter(lockObj, 2000);
            if (!entered)
            {
                check(Name + " [前置失败: 无法取得 _lock]", false);
                try { cap.Dispose(); } catch { }
                return;
            }

            // 循环保持停在帧处理里，直到本用例结束。
            // 这样 finished 一定不会被 Set，StopCapture 的等待必然走满 1 秒，
            // 时序完全确定：它在 t≈1000ms 走到"等待之后"那段代码，
            // 而 _lock 从 t≈250ms 起一直被按住到 t≈3250ms。
            Thread.Sleep(HoldLockMs);
            Monitor.Exit(lockObj);

            // 现在才放行循环，让后台线程有机会收尾退出
            try { releaseFrame.Set(); } catch { }

            bool finished = stopper.Wait(HoldLockMs + StopWaitMs + 3000);
            long elapsed = -1;
            try { elapsed = stopper.Result; } catch { }

            check(Name
                  + (finished
                        ? $" (耗时 {elapsed}ms，期间按住锁 {HoldLockMs}ms)"
                        : " [卡死]"),
                  finished && elapsed >= 0 && elapsed < AllowedMs);

            try { cap.Dispose(); } catch { }
        }

        /// <summary>
        /// 取出组件内部的锁对象。
        ///
        /// 白盒取：<c>_lock</c> 是私有字段，而"等待之后不再取锁"这条不变量
        /// 只有把锁按住才能确定性验证。这里刻意反射——拿到的是同一个
        /// object 实例，Monitor 按对象身份同步。
        /// </summary>
        private static object? GetPrivateLockObject(VideoCaptureComponent cap)
            => typeof(VideoCaptureComponent)
                .GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(cap);

        /// <summary>
        /// 重复 Dispose 必须是空操作，且不因上一轮残留状态而阻塞。
        /// </summary>
        private static void DisposeCore(Action<string, bool> check)
        {
            string video = MakeVideo();
            var cap = new VideoCaptureComponent();

            cap.SourceType = VideoSourceType.FileReplay;
            cap.ReplayPath = video;
            cap.ReplayLoop = true;
            cap.ReplayFps = 60;

            bool started = cap.StartCapture();
            Thread.Sleep(600);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            cap.Dispose();
            cap.Dispose();
            cap.Dispose();
            sw.Stop();

            check("shutdown: Dispose is idempotent and never blocks twice"
                  + $" (三次共 {sw.ElapsedMilliseconds}ms, started={started})",
                  sw.ElapsedMilliseconds < AllowedMs + HoldLockMs);
        }

        private static string MakeVideo()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "vp_shutdown_test_" + Environment.ProcessId + ".avi");
            if (File.Exists(path)) return path;

            using var writer = new VideoWriter(path, FourCC.MJPG, 60.0,
                new OpenCvSharp.Size(160, 120));
            using var mat = new Mat(120, 160, MatType.CV_8UC3);
            for (int i = 0; i < 120; i++)
            {
                mat.SetTo(new Scalar(30, 30, 30));
                writer.Write(mat);
            }
            return path;
        }
    }
}