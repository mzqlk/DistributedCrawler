namespace CrawlerCore.Tests.Integration;

/// <summary>
/// 端到端集成测试集合：集合内串行执行，且不与其他测试集合并行
/// （测试共享全局 Serilog 日志与 Worker 的插件目录，并且对时间敏感）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationTestCollection
{
    public const string Name = "Master-Worker 端到端集成测试";

    /// <summary>Trait 名称与取值：dotnet test --filter "Category=Integration"</summary>
    public const string Category = "Category";
    public const string Integration = "Integration";
}
