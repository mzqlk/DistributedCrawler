using System.Reflection;
using CrawlerCore.Infrastructure;
using CrawlerWorker.Services.Engine;
using Microsoft.Extensions.Options;

namespace CrawlerCore.Tests.Plugins;

/// <summary>插件打包（W-04）与加载隔离（W-18）。只使用临时目录，测试结束时删除</summary>
public sealed class PluginManagerTests : IDisposable
{
    private readonly string _pluginDirectory = Path.Combine(Path.GetTempPath(), "DistributedCrawler.PluginTests", Guid.NewGuid().ToString("N"));

    public PluginManagerTests()
    {
        Directory.CreateDirectory(_pluginDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pluginDirectory, recursive: true); } catch { /* 插件 DLL 可能仍被加载上下文占用，不影响结果 */ }
    }

    private static PluginManager CreateManager(string directory)
        => new(Options.Create(new CrawlerSettings { PluginDirectory = directory }));

    /// <summary>测试输出目录中的 MockPlugin.dll（测试项目直接引用了 MockPlugin）</summary>
    private static string MockPluginAssembly => Path.Combine(AppContext.BaseDirectory, "MockPlugin.dll");

    private void CopyPlugin(string subDirectory, string fileName)
    {
        var target = Path.Combine(_pluginDirectory, subDirectory);
        Directory.CreateDirectory(target);
        File.Copy(MockPluginAssembly, Path.Combine(target, fileName));
    }

    [Fact(DisplayName = "W-04：本地构建的 Worker 输出目录 Plugins/ 中有 ThesisPlugin（每个插件一个子目录），并能加载出 ArXivThesisPlugin")]
    public void WorkerBuildOutput_ContainsLoadableThesisPlugin()
    {
        // 测试程序集位于 CrawlerCore.Tests/bin/<配置>/net10.0-windows/，Worker 输出位于 CrawlerWorker/bin/<配置>/net10.0/
        var configuration = typeof(PluginManagerTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var workerPlugins = Path.Combine(repositoryRoot, "CrawlerWorker", "bin", configuration, "net10.0", "Plugins");

        Assert.True(File.Exists(Path.Combine(workerPlugins, "ThesisPlugin", "ThesisPlugin.dll")), $"缺少 {workerPlugins}\\ThesisPlugin\\ThesisPlugin.dll");
        Assert.True(File.Exists(Path.Combine(workerPlugins, "ThesisPlugin", "HtmlAgilityPack.dll")), "ThesisPlugin 的依赖应随插件部署");
        Assert.False(File.Exists(Path.Combine(workerPlugins, "ThesisPlugin", "CrawlerCore.dll")), "CrawlerCore 由 Worker 提供，不应随插件部署");

        var plugin = CreateManager(workerPlugins).GetPlugin("ArXivThesisPlugin");

        Assert.NotNull(plugin);
        Assert.Equal("1.4.0", plugin.Version);
        // 插件在自己的加载上下文中，但插件接口来自 Worker 的 CrawlerCore
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly));
    }

    [Fact(DisplayName = "W-18：插件目录中有损坏的 DLL 时，其他插件照常加载")]
    public void BrokenAssembly_DoesNotPreventOthers()
    {
        File.WriteAllBytes(Path.Combine(_pluginDirectory, "Broken.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 });
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, "AlsoBroken"));
        File.WriteAllText(Path.Combine(_pluginDirectory, "AlsoBroken", "AlsoBroken.dll"), "not an assembly");
        CopyPlugin("MockPlugin", "MockPlugin.dll");

        var manager = CreateManager(_pluginDirectory);

        Assert.NotNull(manager.GetPlugin("mocktestplugin"));
        Assert.Single(manager.GetAllPlugins());
    }

    [Fact(DisplayName = "W-18：同名插件只保留按路径排序后的第一个（子目录优先于直接放置的 DLL）")]
    public void DuplicatePluginNames_FirstByPathWins()
    {
        CopyPlugin("MockPlugin", "MockPlugin.dll");
        File.Copy(MockPluginAssembly, Path.Combine(_pluginDirectory, "MockPlugin.dll"));

        var manager = CreateManager(_pluginDirectory);

        var plugin = Assert.Single(manager.GetAllPlugins());
        Assert.Equal(Path.Combine(_pluginDirectory, "MockPlugin", "MockPlugin.dll"), plugin.GetType().Assembly.Location);
    }

    [Fact(DisplayName = "W-18：插件目录可配置，首次使用时才加载；目录不存在时没有插件")]
    public void MissingDirectory_NoPlugins()
    {
        var manager = CreateManager(Path.Combine(_pluginDirectory, "does-not-exist"));
        Assert.Null(manager.GetPlugin("MockTestPlugin"));
        Assert.Empty(manager.GetAllPlugins());
    }

    [Fact(DisplayName = "W-18：重新加载期间 GetPlugin 不会返回 null")]
    public async Task Reload_DoesNotExposeEmptyTable()
    {
        CopyPlugin("MockPlugin", "MockPlugin.dll");
        var manager = CreateManager(_pluginDirectory);
        Assert.NotNull(manager.GetPlugin("MockTestPlugin"));

        // 每次重新加载都会新建加载上下文，次数不宜过多
        var reloads = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++) manager.LoadAllPlugins();
        });
        var misses = 0;
        while (!reloads.IsCompleted)
        {
            if (manager.GetPlugin("MockTestPlugin") == null) misses++;
        }
        await reloads;

        Assert.Equal(0, misses);
    }
}
