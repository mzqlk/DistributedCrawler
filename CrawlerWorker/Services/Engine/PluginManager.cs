using CrawlerCore.Common;
using CrawlerCore.Interfaces;
using CrawlerCore.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CrawlerWorker.Services.Engine
{
    /// <summary>
    /// Worker端插件管理器
    /// </summary>
    public sealed class PluginManager : IPluginManager
    {
        private readonly ConcurrentDictionary<string, ICrawlerPlugin> _pluginCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");

        public PluginManager()
        {
            if (!Directory.Exists(_pluginDirectory))
            {
                Directory.CreateDirectory(_pluginDirectory);
                LogHelper.Info($"插件目录初始化: {_pluginDirectory}");
            }
            // 构造函数自动加载插件
            LoadAllPlugins();
        }

        /// <summary>
        /// 批量加载所有插件
        /// </summary>
        public void LoadAllPlugins()
        {
            try
            {
                _pluginCache.Clear();
                var dllFiles = Directory.GetFiles(_pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly);

                if (!dllFiles.Any())
                {
                    LogHelper.Warn("插件目录无可用DLL");
                    return;
                }

                foreach (var dllPath in dllFiles) LoadPluginFromFile(dllPath);
                LogHelper.Info($"插件加载完成，总计: {_pluginCache.Count} 个");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, "插件批量加载异常");
            }
        }

        private void LoadPluginFromFile(string dllPath)
        {
            try
            {
                var assembly = Assembly.LoadFrom(dllPath);
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(ICrawlerPlugin).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is not ICrawlerPlugin plugin) continue;

                    if (_pluginCache.TryAdd(plugin.Name, plugin))
                        LogHelper.Info($"加载成功: {plugin.Name} v{plugin.Version}");
                    else
                        LogHelper.Warn($"插件重复: {plugin.Name}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"DLL加载失败: {Path.GetFileName(dllPath)}");
            }
        }

        /// <summary>
        /// 获取插件实例（大小写不敏感）
        /// </summary>
        public ICrawlerPlugin? GetPlugin(string pluginName)
        {
            _pluginCache.TryGetValue(pluginName, out var plugin);
            return plugin;
        }

        public IReadOnlyList<ICrawlerPlugin> GetAllPlugins() => _pluginCache.Values.ToList();
    }
}