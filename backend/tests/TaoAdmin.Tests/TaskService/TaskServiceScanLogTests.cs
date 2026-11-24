using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace TaoAdmin.Tests.TaskService;

/// <summary>
/// 接口扫描失败回调实效断言：TaskServiceScanLog.ScanFailed 必须把异常以 Warning
/// 级别写入 Serilog（原内联 lambda 在测试环境不可达，提为方法组后在此做实效断言）。
/// </summary>
public class TaskServiceScanLogTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events;
        public CollectingSink(List<LogEvent> events) => _events = events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    [Fact]
    public void ScanFailed_写入单条Warning且携带异常()
    {
        var events = new List<LogEvent>();
        var original = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new CollectingSink(events))
                .CreateLogger();

            TaskServiceScanLog.ScanFailed(new InvalidOperationException("boom"));
        }
        finally
        {
            Log.Logger = original;
        }

        var single = Assert.Single(events);
        Assert.Equal(LogEventLevel.Warning, single.Level);
        Assert.Contains("接口扫描初始化失败", single.RenderMessage());
        Assert.NotNull(single.Exception);
        Assert.IsType<InvalidOperationException>(single.Exception);
    }
}
