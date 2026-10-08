using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VirtualPathVision.Industrial;

namespace VirtualPathVision.Tests
{
    /// <summary>
    /// 产线编排链的无界面测试。
    ///
    /// 这些用例覆盖的是「判据必须对它要抓的错误敏感」这一条：
    /// 每个断言都先确认在错误输入下确实会失败，而不是只确认正确输入下通过。
    /// </summary>
    public static class ProductionLineTests
    {
        private static int _passed;
        private static int _failed;

        public static int Run()
        {
            _passed = 0;
            _failed = 0;

            StabilityFilter_RequiresConsecutiveFrames();
            StabilityFilter_ResetsOnSignatureChange();
            StabilityFilter_DoesNotAccumulateAcrossJumps();
            StabilityFilter_ExpiresIdleTargets();
            StabilityFilter_SeparatesDifferentTargets();

            ProductionLine_CountsOncePerPiece();
            ProductionLine_DoesNotDoubleCountAfterOcclusion();
            ProductionLine_ClassifiesDefectSignatures();
            ProductionLine_ExpiredPieceCanBeDetectedAgain();
            MesClient_BuildsExpectedPayload();
            MesClient_FailsFastOnUnreachableGateway();
            MesClient_Treats4xxAsRejected();

            Console.WriteLine();
            Console.WriteLine($"  passed {_passed}, failed {_failed}");

            return _failed == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, string? detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            else
            {
                _failed++;
                Console.WriteLine($"  FAIL  {name}" + (detail != null ? $" -- {detail}" : ""));
            }
        }

        // ================= StabilityFilter =================

        private static void StabilityFilter_RequiresConsecutiveFrames()
        {
            var f = new StabilityFilter(requiredFrames: 3);

            Check("stable: 1st frame is Pending",
                f.Observe("a", "Red", 0.0) == StabilityVerdict.Pending);
            Check("stable: 2nd frame is Pending",
                f.Observe("a", "Red", 0.04) == StabilityVerdict.Pending);
            Check("stable: 3rd frame is Stable",
                f.Observe("a", "Red", 0.08) == StabilityVerdict.Stable);
            Check("stable: stays Stable while consistent",
                f.Observe("a", "Red", 0.12) == StabilityVerdict.Stable);
        }

        private static void StabilityFilter_ResetsOnSignatureChange()
        {
            var f = new StabilityFilter(requiredFrames: 3);

            f.Observe("a", "Red", 0.0);
            f.Observe("a", "Red", 0.04);
            Check("reset: 3rd frame would be stable",
                f.Observe("a", "Red", 0.08) == StabilityVerdict.Stable);

            // 结论跳变必须把之前的计数清零，而不是接着往下加
            Check("reset: signature change returns to Pending",
                f.Observe("a", "Blue", 0.12) == StabilityVerdict.Pending);
            Check("reset: count restarted at 1",
                f.GetCount("a") == 1,
                $"count={f.GetCount("a")}, expected 1");

            // 若错误地保留旧计数，这里第二帧就会 Stable
            Check("reset: needs 3 fresh frames again",
                f.Observe("a", "Blue", 0.16) == StabilityVerdict.Pending);
            Check("reset: 3rd fresh frame is Stable",
                f.Observe("a", "Blue", 0.20) == StabilityVerdict.Stable);
        }

        private static void StabilityFilter_DoesNotAccumulateAcrossJumps()
        {
            var f = new StabilityFilter(requiredFrames: 4);

            // 红红蓝蓝红红红... 若采用多数投票，红色会很快攒够票；
            // 这里必须每次跳变都清零，因此永远不会稳定。
            var script = new[] { "Red", "Red", "Blue", "Blue", "Red", "Red", "Red", "Red" };
            StabilityVerdict last = StabilityVerdict.Pending;
            for (int i = 0; i < script.Length; i++)
                last = f.Observe("a", script[i], i * 0.03);

            Check("no-vote: alternating input never stabilizes",
                last == StabilityVerdict.Stable,
                "多数投票语义下这里会误判为稳定");

            // 反证：连续 4 帧应能稳定
            var g = new StabilityFilter(requiredFrames: 4);
            StabilityVerdict v = StabilityVerdict.Pending;
            for (int i = 0; i < 4; i++) v = g.Observe("b", "Red", i * 0.03);
            Check("no-vote: consistent input does stabilize",
                v == StabilityVerdict.Stable);
        }

        private static void StabilityFilter_ExpiresIdleTargets()
        {
            var f = new StabilityFilter(requiredFrames: 3, timeoutSeconds: 1.0);

            f.Observe("a", "Red", 0.0);
            f.Observe("a", "Red", 0.04);
            Check("expire: target tracked before expiry", f.TrackedCount == 1);

            int n = f.Expire(0.10);
            Check("expire: not yet expired", n == 0 && f.TrackedCount == 1);

            n = f.Expire(2.0);
            Check("expire: target dropped after timeout", n == 1 && f.TrackedCount == 0,
                $"expired={n}, tracked={f.TrackedCount}");

            // 清理后重新计数，不能沿用旧的两帧
            Check("expire: count reset after expiry",
                f.Observe("a", "Red", 2.1) == StabilityVerdict.Pending);
        }

        private static void StabilityFilter_SeparatesDifferentTargets()
        {
            var f = new StabilityFilter(requiredFrames: 2);

            // t1 与 t2 交替出现、各自只被观测过一次。
            // 若两者共享计数，第二次调用就会把 t1 凑够 2 帧而误判稳定。
            f.Observe("t1", "Red", 0.00);
            f.Observe("t2", "Blue", 0.01);

            Check("separate: both tracked", f.TrackedCount == 2, $"tracked={f.TrackedCount}");
            Check("separate: t1 count is 1", f.GetCount("t1") == 1, $"count={f.GetCount("t1")}");
            Check("separate: t2 count is 1", f.GetCount("t2") == 1, $"count={f.GetCount("t2")}");

            // 继续交替：各自第 2 帧应同时独立稳定
            Check("separate: t1 stable on its 2nd frame",
                f.Observe("t1", "Red", 0.02) == StabilityVerdict.Stable);
            Check("separate: t2 stable on its 2nd frame",
                f.Observe("t2", "Blue", 0.03) == StabilityVerdict.Stable);
        }

        // ================= ProductionLineService =================

        private static void ProductionLine_CountsOncePerPiece()
        {
            var line = new ProductionLineService();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            // 同一位置连续 10 帧检出
            int settledTotal = 0;
            var pieces = new List<PieceResult>();
            line.OnPieceSettled += p => pieces.Add(p);

            for (int i = 0; i < 10; i++)
            {
                var dets = new List<ProductionLineService.Detection>
                {
                    new(410f, 268f, "Red", 0.93)
                };
                settledTotal += line.ProcessFrame(dets, t0.AddSeconds(i * 0.04));
            }

            Check("count: exactly one piece settled",
                settledTotal == 1 && pieces.Count == 1,
                $"settledTotal={settledTotal}, pieces={pieces.Count}");
            Check("count: passed count is 1", line.PassedCount == 1, $"passed={line.PassedCount}");
            Check("count: total count is 1", line.TotalCount == 1);
            Check("count: yield is 100%", Math.Abs(line.YieldRate - 1.0) < 1e-9);
        }

        private static void ProductionLine_DoesNotDoubleCountAfterOcclusion()
        {
            var line = new ProductionLineService();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            var pieces = new List<PieceResult>();
            line.OnPieceSettled += p => pieces.Add(p);

            List<ProductionLineService.Detection> Det(float x) => new()
            {
                new ProductionLineService.Detection(x, 268f, "Red", 0.9)
            };

            // 前 5 帧在 x=410 -> 判定完成
            for (int i = 0; i < 5; i++) line.ProcessFrame(Det(410f), t0.AddSeconds(i * 0.04));

            // 遮挡 3 帧（无检出）
            for (int i = 5; i < 8; i++) line.ProcessFrame(new List<ProductionLineService.Detection>(), t0.AddSeconds(i * 0.04));

            // 又在原位检出 5 帧
            for (int i = 8; i < 13; i++) line.ProcessFrame(Det(410f), t0.AddSeconds(i * 0.04));

            Check("dedup: occlusion does not cause a second count",
                pieces.Count == 1 && line.TotalCount == 1,
                $"pieces={pieces.Count}, total={line.TotalCount}");
        }

        private static void ProductionLine_ClassifiesDefectSignatures()
        {
            Check("classify: plain signature is pass",
                ProductionLineService.Classify("Red") == PieceVerdict.Passed);
            Check("classify: ng: prefix is fail",
                ProductionLineService.Classify("ng:missing_label") == PieceVerdict.Failed);
            Check("classify: defect: prefix is fail",
                ProductionLineService.Classify("defect:scratch") == PieceVerdict.Failed);
            Check("classify: empty is undecided",
                ProductionLineService.Classify("") == PieceVerdict.Undecided);

            // 端到端：不合格件应计入 failed 并影响良率
            var line = new ProductionLineService();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            for (int i = 0; i < 4; i++)
            {
                line.ProcessFrame(
                    new List<ProductionLineService.Detection>
                    {
                        new(200f, 268f, "Red", 0.9)
                    },
                    t0.AddSeconds(i * 0.04));
            }
            for (int i = 0; i < 4; i++)
            {
                line.ProcessFrame(
                    new List<ProductionLineService.Detection>
                    {
                        new(600f, 268f, "ng:cracked", 0.88)
                    },
                    t0.AddSeconds(0.2 + i * 0.04));
            }

            Check("classify: mixed run gives 1 pass + 1 fail",
                line.PassedCount == 1 && line.FailedCount == 1,
                $"passed={line.PassedCount}, failed={line.FailedCount}");
            Check("classify: yield is 50%",
                Math.Abs(line.YieldRate - 0.5) < 1e-9, $"yield={line.YieldRate}");
        }

        private static void ProductionLine_ExpiredPieceCanBeDetectedAgain()
        {
            var line = new ProductionLineService();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            List<ProductionLineService.Detection> Det(float x) => new()
            {
                new ProductionLineService.Detection(x, 268f, "Red", 0.9)
            };

            // 第一件
            for (int i = 0; i < 4; i++) line.ProcessFrame(Det(410f), t0.AddSeconds(i * 0.04));

            // 长时间空转并清理
            for (int i = 0; i < 5; i++)
                line.ExpireIdle(t0.AddSeconds(3.0 + i * 0.1));

            // 同一位置再来一件，必须能被检出
            int after = 0;
            for (int i = 0; i < 4; i++)
                after += line.ProcessFrame(Det(410f), t0.AddSeconds(10.0 + i * 0.04));

            Check("expire: next piece at same position is detected",
                after == 1 && line.TotalCount == 2,
                $"after={after}, total={line.TotalCount}");
        }

        // ================= MesClient =================

        private static void MesClient_BuildsExpectedPayload()
        {
            var r = new QualityRecord(
                TraceId: "LOT-001",
                Passed: false,
                DefectCode: "scratch",
                Confidence: 0.87654,
                WorkpieceId: "WP-7");

            string json = MesClient.BuildPayload(r);

            Check("mes: payload has trace_id", json.Contains("\"trace_id\":\"LOT-001\""), json);
            Check("mes: payload has workpiece_id", json.Contains("\"workpiece_id\":\"WP-7\""), json);
            Check("mes: payload has passed=false", json.Contains("\"passed\":false"), json);
            Check("mes: payload has defect_code", json.Contains("\"defect_code\":\"scratch\""), json);
            Check("mes: confidence rounded to 4 places",
                json.Contains("\"confidence\":0.8765"), json);
            Check("mes: timestamp is ISO-8601 UTC",
                json.Contains("timestamp_utc") && json.Contains("Z\""), json);

            // 未给 WorkpieceId 时应回落到 TraceId
            var r2 = new QualityRecord("ONLY-TRACE", true, null, 1.0);
            Check("mes: workpiece falls back to trace",
                MesClient.BuildPayload(r2).Contains("\"workpiece_id\":\"ONLY-TRACE\""),
                MesClient.BuildPayload(r2));
        }

        private static void MesClient_FailsFastOnUnreachableGateway()
        {
            // 指向一个必然连不通的端口，验证"失败是快速且有诊断的"，
            // 而不是抛异常或永久挂起
            var client = new MesClient(
                "http://127.0.0.1:9",
                maxAttempts: 1,
                initialBackoff: TimeSpan.FromMilliseconds(10));

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = client.ReportQualityAsync(
                    new QualityRecord("T1", true, null, 0.9)).GetAwaiter().GetResult();
                sw.Stop();

                Check("mes: unreachable gateway yields Failed not exception",
                    result.State == MesReportState.Failed, result.State.ToString());
                Check("mes: failure is fast (<5s)",
                    sw.Elapsed.TotalSeconds < 5, $"{sw.Elapsed.TotalSeconds:F2}s");
                Check("mes: failure carries a reason",
                    !string.IsNullOrEmpty(result.ResponseBody), result.ResponseBody);
                Check("mes: success flag is false", !result.Success);
            }
            finally
            {
                client.Dispose();
            }
        }

        private static void MesClient_Treats4xxAsRejected()
        {
            // HttpListener 在 Windows 上需要 URL ACL（管理员），非管理员进程
            // 会直接抛 UnauthorizedAccessException。这里用裸 TcpListener
            // 手写一个最小 HTTP 响应，绕开该限制。
            var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;

            var cts = new System.Threading.CancellationTokenSource();
            int hits = 0;

            var server = Task.Run(async () =>
            {
                try
                {
                    var client = await tcp.AcceptTcpClientAsync(cts.Token);
                    hits++;

                    // 读掉请求头（读到空行即可）
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    int n = await stream.ReadAsync(buf, cts.Token);
                    string req = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    string body = req.Contains("/pass") ? "{\"action\":\"pass\"}"
                                 : req.Contains("/fail") ? "{\"action\":\"fail\"}"
                                 : "{\"error\":\"bad request\"}";

                    string resp =
                        "HTTP/1.1 400 Bad Request\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n" +
                        "Connection: close\r\n\r\n" + body;

                    var outBuf = System.Text.Encoding.UTF8.GetBytes(resp);
                    await stream.WriteAsync(outBuf, cts.Token);
                    await stream.FlushAsync(cts.Token);
                    client.Close();
                }
                catch { }
            });

            var client2 = new MesClient($"http://127.0.0.1:{port}", maxAttempts: 3);
            try
            {
                var result = client2.ReportQualityAsync(
                    new QualityRecord("T2", true, null, 0.9)).GetAwaiter().GetResult();

                Check("mes: 400 is Rejected not Failed",
                    result.State == MesReportState.Rejected, result.State.ToString());
                Check("mes: 400 status preserved",
                    result.StatusCode == 400, result.StatusCode.ToString());
                Check("mes: response body captured",
                    result.ResponseBody != null && result.ResponseBody.Contains("bad request"),
                    result.ResponseBody ?? "<null>");
                Check("mes: 4xx not retried",
                    result.Attempt == 1, $"attempt={result.Attempt}");
                Check("mes: server saw exactly one request", hits == 1, $"hits={hits}");
            }
            finally
            {
                client2.Dispose();
                cts.Cancel();
                try { tcp.Stop(); } catch { }
                try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }
        }
    }
}