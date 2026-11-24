using Xunit;

namespace TaoAdmin.Tests;

/// <summary>
/// 日志回退串行集合：未配置 Monitor:LogDir 的探针宿主会把服务日志写到运行目录 logs/，
/// 与 MonitorGapTests 的「回退目录清理」存在删除竞争，统一排入本集合串行执行。
/// </summary>
[CollectionDefinition("LogDirFallback")]
public class LogDirFallbackCollection;
