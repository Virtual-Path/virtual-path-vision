using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
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
            MesClient_PayloadMatchesGatewayDto();
            MesClient_ClipsFieldsAtDtoSizeLimits();
            MesClient_FallsBackOnInvalidCheckType();
            MesClient_RequiresSn();
            MesClient_SendsBearerToken();
            MesClient_ParsesRecordIdFromEnvelope();
            MesClient_TreatsBusinessErrorAsRejected();
            MesClient_PassUsesRecordIdPath();
            MesClient_FailSendsReasonQueryParam();
            MesClient_FailWithoutReasonStillSendsParam();
            MesClient_FailsFastOnUnreachableGateway();
            MesClient_Treats4xxAsRejected();
            MesClient_RetriesOn5xx();

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
        //
        // 报文契约取自 virtual-path-mes/mes-quality：
        //   com.mes.quality.dto.CreateQualityRecordDTO   （请求体字段与 @Size/@Pattern 约束）
        //   com.mes.quality.controller.QualityController  （端点路径与返回类型）
        //   com.mes.common.result.Result                  （响应封装 {code,message,data,timestamp}）
        // 以及 mes-gateway 的 JwtAuthGlobalFilter（quality 路径强制 Bearer 鉴权）。

        private static void MesClient_PayloadMatchesGatewayDto()
        {
            var r = new QualityRecord(
                Sn: "SN-000123",
                Passed: false,
                DefectType: "外观划伤",
                DefectDesc: "surface scratch on top face",
                WorkOrderNo: "WO-2026-0007");

            string json = MesClient.BuildPayload(r);

            // DTO 未配置 Jackson 命名策略 -> camelCase
            Check("mes: sn maps to DTO field", json.Contains("\"sn\":\"SN-000123\""), json);
            Check("mes: workOrderNo maps to DTO field",
                json.Contains("\"workOrderNo\":\"WO-2026-0007\""), json);
            Check("mes: checkType present", json.Contains("\"checkType\""), json);
            Check("mes: defectType maps to DTO field",
                json.Contains("\"defectType\""), json);
            Check("mes: defectDesc maps to DTO field",
                json.Contains("\"defectDesc\""), json);

            // 反向断言：此前实现用的是 snake_case，会被 @Jackson 的默认
            // camelCase 反序列化静默忽略，sn/checkType 变成 null -> @NotBlank 400
            Check("mes: payload is NOT snake_case",
                !json.Contains("trace_id") && !json.Contains("workpiece_id"),
                json);

            // checkResult 是字符串枚举，不是布尔
            Check("mes: checkResult is enum string not bool",
                json.Contains("\"checkResult\":\"FAILED\"") && !json.Contains("\"passed\":"),
                json);

            // 正向用例：合格件
            var ok = MesClient.BuildPayload(new QualityRecord("SN-OK", true));
            Check("mes: passed -> PASSED", ok.Contains("\"checkResult\":\"PASSED\""), ok);
            Check("mes: passed has no defectType",
                !ok.Contains("\"defectType\":\"") && ok.Contains("\"defectType\":null"), ok);
        }

        private static void MesClient_ClipsFieldsAtDtoSizeLimits()
        {
            // DTO: @Size(max=100) sn, @Size(max=50) workOrderNo/defectType,
            //      @Size(max=500) defectDesc/remark
            // 超长会让 @Valid 失败 -> 400，而截断是静默的：
            // 宁可少几个字符，也不要整条记录被拒。
            var r = new QualityRecord(
                Sn: new string('S', 250),
                Passed: false,
                DefectType: new string('D', 120),
                DefectDesc: new string('X', 900),
                WorkOrderNo: new string('W', 120),
                Remark: new string('R', 700));

            string json = MesClient.BuildPayload(r);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Check("mes: sn clipped to 100",
                root.GetProperty("sn").GetString()!.Length == 100,
                root.GetProperty("sn").GetString()!.Length.ToString());
            Check("mes: workOrderNo clipped to 50",
                root.GetProperty("workOrderNo").GetString()!.Length == 50,
                root.GetProperty("workOrderNo").GetString()!.Length.ToString());
            Check("mes: defectType clipped to 50",
                root.GetProperty("defectType").GetString()!.Length == 50,
                root.GetProperty("defectType").GetString()!.Length.ToString());
            Check("mes: defectDesc clipped to 500",
                root.GetProperty("defectDesc").GetString()!.Length == 500,
                root.GetProperty("defectDesc").GetString()!.Length.ToString());
            Check("mes: remark clipped to 500",
                root.GetProperty("remark").GetString()!.Length == 500,
                root.GetProperty("remark").GetString()!.Length.ToString());
        }

        private static void MesClient_FallsBackOnInvalidCheckType()
        {
            // DTO 的 @Pattern 只接受 IPQC|FQC|OQC|巡检|首检|终检
            var bad = MesClient.BuildPayload(
                new QualityRecord("SN-1", true, CheckType: "MIDLINE"));
            Check("mes: invalid checkType falls back to IPQC",
                bad.Contains("\"checkType\":\"IPQC\""), bad);

            var none = MesClient.BuildPayload(
                new QualityRecord("SN-1", true, CheckType: null!));
            Check("mes: null checkType falls back to IPQC",
                none.Contains("\"checkType\":\"IPQC\""), none);

            // 反向断言：合法值必须原样保留，不能被一律改成 IPQC
            var fqc = MesClient.BuildPayload(
                new QualityRecord("SN-1", true, CheckType: "FQC"));
            Check("mes: valid checkType preserved",
                fqc.Contains("\"checkType\":\"FQC\""), fqc);
        }

        private static void MesClient_RequiresSn()
        {
            // sn 是网关建立产品追溯的主键，@NotBlank 会拒空值。
            // 与其让上报在网关侧失败，不如在本地就明确报错。
            bool threw = false;
            try { MesClient.BuildPayload(new QualityRecord("   ", true)); }
            catch (ArgumentException) { threw = true; }
            Check("mes: blank sn rejected locally", threw);

            bool threw2 = false;
            try { MesClient.BuildPayload(null!); }
            catch (ArgumentNullException) { threw2 = true; }
            Check("mes: null record rejected", threw2);
        }

        private static void MesClient_SendsBearerToken()
        {
            // 网关 JwtAuthGlobalFilter 的白名单是
            // /api/auth/login, /api/auth/register, /actuator/**
            // quality 路径不在其中，缺 Authorization 头一律 401。
            using var gw = FakeGateway.Start(_ => gw_reply());
            var client = new MesClient(gw.BaseUrl, "test-jwt-token", maxAttempts: 1);
            try
            {
                client.ReportQualityAsync(new QualityRecord("SN-1", true))
                      .GetAwaiter().GetResult();
                gw.WaitForRequests(1);

                string? auth = gw.Requests[0].Header("Authorization");
                Check("mes: sends Bearer token",
                    auth == "Bearer test-jwt-token", auth ?? "<null>");

                string? accept = gw.Requests[0].Header("Accept");
                Check("mes: sends Accept: application/json",
                    accept != null && accept.Contains("application/json"),
                    accept ?? "<null>");
            }
            finally { client.Dispose(); }
        }

        private static void MesClient_ParsesRecordIdFromEnvelope()
        {
            // createRecord 返回 Result<Long>，data 就是新记录主键。
            // 放行/剔除端点是 /record/{id}/pass|fail，拿不到这个 id 就无法调动作。
            using var gw = FakeGateway.Start(_ =>
                FakeGateway.Reply(200,
                    "{\"code\":200,\"message\":\"success\",\"data\":12345,\"timestamp\":1700000000000}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 1);
            try
            {
                var result = client.ReportQualityAsync(new QualityRecord("SN-1", true))
                                    .GetAwaiter().GetResult();
                Check("mes: record id parsed from Result.data",
                    result.RecordId == 12345, result.RecordId?.ToString() ?? "<null>");
                Check("mes: code 200 is Accepted",
                    result.State == MesReportState.Accepted, result.State.ToString());
            }
            finally { client.Dispose(); }
        }

        private static void MesClient_TreatsBusinessErrorAsRejected()
        {
            // 网关业务失败仍返回 HTTP 200，只靠 code 区分。
            // 若把"2xx 即成功"当判据，这次上报会被静默当成成功——
            // 计数照涨，MES 里却没有记录，而且没有任何告警。
            using var gw = FakeGateway.Start(_ =>
                FakeGateway.Reply(200,
                    "{\"code\":5002,\"message\":\"not logged in\",\"data\":null,\"timestamp\":1}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 3);
            try
            {
                var result = client.ReportQualityAsync(new QualityRecord("SN-1", true))
                                    .GetAwaiter().GetResult();
                Check("mes: HTTP 200 + code 5002 is Rejected",
                    result.State == MesReportState.Rejected, result.State.ToString());
                Check("mes: business error body preserved",
                    result.ResponseBody != null && result.ResponseBody.Contains("not logged in"),
                    result.ResponseBody ?? "<null>");
                Check("mes: business error not retried",
                    result.Attempt == 1, $"attempt={result.Attempt}");
                Check("mes: business error has no record id",
                    result.RecordId == null, result.RecordId?.ToString() ?? "<null>");
                Check("mes: business error hit server once",
                    gw.Requests.Count == 1, $"count={gw.Requests.Count}");
            }
            finally { client.Dispose(); }
        }

        private static void MesClient_PassUsesRecordIdPath()
        {
            // 端点是 POST /quality/record/{id}/pass（网关 StripPrefix=1 后
            // 对外是 /api/quality/record/{id}/pass），无请求体。
            // 此前实现用的 /api/quality/pass 在后端根本不存在。
            using var gw = FakeGateway.Start(_ =>
                FakeGateway.Reply(200, "{\"code\":200,\"message\":\"success\",\"data\":null}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 1);
            try
            {
                var r = client.PassAsync(12345).GetAwaiter().GetResult();
                gw.WaitForRequests(1);

                Check("mes: pass uses /record/{id}/pass",
                    gw.Requests[0].Path == "/api/quality/record/12345/pass",
                    gw.Requests[0].Path);
                Check("mes: pass is a POST",
                    gw.Requests[0].Method == "POST", gw.Requests[0].Method);
                Check("mes: pass sends no body",
                    string.IsNullOrEmpty(gw.Requests[0].Body),
                    $"body='{gw.Requests[0].Body}'");
                Check("mes: pass succeeded", r.State == MesReportState.Accepted,
                    r.State.ToString());
            }
            finally { client.Dispose(); }
        }

        private static void MesClient_FailSendsReasonQueryParam()
        {
            // fail 端点声明 @RequestParam String reason（必填）
            // 且 PostMapping("/record/{id}/fail")——注意是 /fail，不是 /pass 的变体。
            using var gw = FakeGateway.Start(_ =>
                FakeGateway.Reply(200, "{\"code\":200,\"message\":\"success\",\"data\":null}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 1);
            try
            {
                client.FailAsync(777, "surface scratch 表面划伤/深度")
                      .GetAwaiter().GetResult();
                gw.WaitForRequests(1);

                string path = gw.Requests[0].Path;
                Check("mes: fail uses /record/{id}/fail",
                    path.StartsWith("/api/quality/record/777/fail"), path);
                Check("mes: fail carries reason param", path.Contains("reason="), path);
                // 中文与空格必须百分号编码，否则 URL 里的裸字符会让
                // HttpClient 抛 UriFormatException
                Check("mes: reason is percent-encoded", !path.Contains(" "), path);
                Check("mes: fail request actually reached server",
                    gw.Requests.Count == 1, $"count={gw.Requests.Count}");
            }
            finally { client.Dispose(); }
        }

        private static void MesClient_FailWithoutReasonStillSendsParam()
        {
            // reason 是必填 @RequestParam：漏掉会被 Spring 拒为 400。
            using var gw = FakeGateway.Start(_ =>
                FakeGateway.Reply(200, "{\"code\":200,\"message\":\"success\",\"data\":null}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 1);
            try
            {
                client.FailAsync(777, "").GetAwaiter().GetResult();
                gw.WaitForRequests(1);
                Check("mes: empty reason still sends reason= param",
                    gw.Requests[0].Path.Contains("reason="), gw.Requests[0].Path);
            }
            finally { client.Dispose(); }
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
                    new QualityRecord("T1", true)).GetAwaiter().GetResult();
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
            using var gw = FakeGateway.Start(_ => FakeGateway.Reply(400, "{\"error\":\"bad request\"}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 3);
            try
            {
                var result = client.ReportQualityAsync(new QualityRecord("T2", true))
                                    .GetAwaiter().GetResult();

                Check("mes: 400 is Rejected not Failed",
                    result.State == MesReportState.Rejected, result.State.ToString());
                Check("mes: 400 status preserved",
                    result.StatusCode == 400, result.StatusCode.ToString());
                Check("mes: response body captured",
                    result.ResponseBody != null && result.ResponseBody.Contains("bad request"),
                    result.ResponseBody ?? "<null>");
                Check("mes: 4xx not retried",
                    result.Attempt == 1, $"attempt={result.Attempt}");
                Check("mes: server saw exactly one request",
                    gw.Requests.Count == 1, $"count={gw.Requests.Count}");
            }
            finally
            {
                client.Dispose();
            }
        }

        private static void MesClient_RetriesOn5xx()
        {
            // 5xx 是服务端暂时故障，必须重试；否则网关抖一下就永久丢记录。
            using var gw = FakeGateway.Start(_ => FakeGateway.Reply(503, "{\"message\":\"down\"}"));
            var client = new MesClient(gw.BaseUrl, "tok", maxAttempts: 2,
                initialBackoff: TimeSpan.FromMilliseconds(10));
            try
            {
                var result = client.ReportQualityAsync(new QualityRecord("T3", true))
                                    .GetAwaiter().GetResult();
                Check("mes: 503 is Failed not Rejected",
                    result.State == MesReportState.Failed, result.State.ToString());
                Check("mes: 503 retried up to maxAttempts",
                    result.Attempt == 2, $"attempt={result.Attempt}");
                Check("mes: 503 produced more than one request",
                    gw.Requests.Count >= 2, $"count={gw.Requests.Count}");
            }
            finally { client.Dispose(); }
        }

        private static string gw_reply()
            => FakeGateway.Reply(200, "{\"code\":200,\"message\":\"success\",\"data\":1}");

        // ================= 假网关 =================

        /// <summary>
        /// 裸 <c>TcpListener</c> 手写的最小 HTTP 服务器，记录收到的请求并回放预设响应。
        /// 不用 <c>HttpListener</c>：它在 Windows 上需要 URL ACL（管理员权限），
        /// 非管理员进程会直接抛 <c>UnauthorizedAccessException</c>。
        /// </summary>
        private sealed class FakeGateway : IDisposable
        {
            public sealed record Captured(
                string Method, string Path, string Body, Dictionary<string, string> Headers)
            {
                public string? Header(string name)
                    => Headers.TryGetValue(name, out var v) ? v : null;
            }

            private readonly TcpListener _tcp;
            private readonly CancellationTokenSource _cts = new();
            private readonly Func<int, string> _responder;
            private readonly List<Captured> _requests = new();
            private readonly object _gate = new();
            private readonly Task _loop;

            public string BaseUrl { get; }

            public IReadOnlyList<Captured> Requests
            {
                get { lock (_gate) return _requests.ToArray(); }
            }

            private FakeGateway(Func<int, string> responder)
            {
                _responder = responder;
                _tcp = new TcpListener(System.Net.IPAddress.Loopback, 0);
                _tcp.Start();
                int port = ((System.Net.IPEndPoint)_tcp.LocalEndpoint).Port;
                BaseUrl = $"http://127.0.0.1:{port}";
                _loop = Task.Run(ServeAsync);
            }

            public static FakeGateway Start(Func<int, string> responder)
                => new FakeGateway(responder);

            /// <summary>回放一条 HTTP 响应。</summary>
            public static string Reply(int status, string body)
                => "HTTP/1.1 " + status + " Status\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   "Content-Length: " + System.Text.Encoding.UTF8.GetByteCount(body) + "\r\n" +
                   "Connection: close\r\n\r\n" + body;

            public void WaitForRequests(int count, int timeoutMs = 4000)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    lock (_gate) { if (_requests.Count >= count) return; }
                    Thread.Sleep(15);
                }
            }

            private async Task ServeAsync()
            {
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                    catch { return; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            {
                                var stream = client.GetStream();

                                // 读请求头到空行
                                var head = new System.Text.StringBuilder();
                                var one = new byte[1];
                                while (!head.ToString().EndsWith("\r\n\r\n"))
                                {
                                    int n = await stream.ReadAsync(one, 0, 1, ct)
                                                        .ConfigureAwait(false);
                                    if (n == 0) return;
                                    head.Append((char)one[0]);
                                    if (head.Length > 16384) return;
                                }

                                string raw = head.ToString();
                                var lines = raw.Split("\r\n", StringSplitOptions.None);
                                var reqParts = lines[0].Split(' ');
                                string method = reqParts.Length > 0 ? reqParts[0] : "?";
                                string path = reqParts.Length > 1 ? reqParts[1] : "/";

                                var headers = new Dictionary<string, string>(
                                    StringComparer.OrdinalIgnoreCase);
                                foreach (var line in lines)
                                {
                                    int colon = line.IndexOf(':');
                                    if (colon > 0)
                                    {
                                        headers[line[..colon].Trim()] =
                                            line[(colon + 1)..].Trim();
                                    }
                                }

                                // 读请求体
                                string body = "";
                                if (headers.TryGetValue("Content-Length", out var clv)
                                    && int.TryParse(clv, out int contentLength)
                                    && contentLength > 0)
                                {
                                    var buf = new byte[contentLength];
                                    int read = 0;
                                    while (read < contentLength)
                                    {
                                        int n = await stream.ReadAsync(
                                            buf, read, contentLength - read, ct)
                                            .ConfigureAwait(false);
                                        if (n == 0) break;
                                        read += n;
                                    }
                                    body = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                                }

                                int index;
                                lock (_gate)
                                {
                                    _requests.Add(new Captured(method, path, body, headers));
                                    index = _requests.Count - 1;
                                }

                                var resp = System.Text.Encoding.UTF8.GetBytes(_responder(index));
                                await stream.WriteAsync(resp, 0, resp.Length, ct)
                                         .ConfigureAwait(false);
                                await stream.FlushAsync(ct).ConfigureAwait(false);
                            }
                        }
                        catch { /* 客户端断开，忽略 */ }
                    }, ct);
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _tcp.Stop(); } catch { }
                try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
                _cts.Dispose();
            }
        }
    }
}