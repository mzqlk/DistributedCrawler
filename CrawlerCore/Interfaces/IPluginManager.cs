using CrawlerCore.Interfaces;

namespace CrawlerWorker.Services.Engine;

/// <summary>
/// Worker端插件管理器
/// </summary>
public interface IPluginManager
{
    /// <summary>
    /// 获取插件实例（大小写不敏感）
    /// </summary>
    ICrawlerPlugin? GetPlugin(string pluginName);

    /// <summary>
    /// 获取所有插件实例（不保证顺序）
    /// </summary>
    /// <returns>插件实例列表</returns>
    IReadOnlyList<ICrawlerPlugin> GetAllPlugins();

    /// <summary>
    /// 批量加载所有插件
    /// </summary>
    void LoadAllPlugins();
}