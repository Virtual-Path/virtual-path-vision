using System;

namespace VirtualPathVision.Tests
{
    /// <summary>
    /// 产线链路测试入口。
    /// </summary>
    /// <remarks>
    /// 刻意不使用 xunit：本工程的测试针对的是时序与协议行为，
    /// 用裸断言 + 明确的退出码更便于在 CI 与本地直接跑
    /// （<c>dotnet run --project VirtualPathVision.Tests</c>）。
    /// </remarks>
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("=== production line pipeline tests ===");
            return ProductionLineTests.Run();
        }
    }
}
