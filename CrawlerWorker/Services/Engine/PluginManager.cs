using CrawlerCore.Infrastructure;
using CrawlerCore.Interfaces;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace CrawlerWorker.Services.Engine
{
    /// <summary>
    /// Worker端插件管理器（W-18）：
    /// <list type="bullet">
    /// <item>插件目录由 <see cref="CrawlerSettings.PluginDirectory"/> 指定（相对路径相对于程序所在目录），首次使用时才加载。</item>
    /// <item>每个插件一个子目录（<c>Plugins/&lt;插件项目名&gt;/&lt;插件项目名&gt;.dll</c>，见 CrawlerWorker.csproj），
    /// 用独立的 <see cref="AssemblyLoadContext"/> 加载，插件自己的依赖按它的 deps.json 从插件目录解析；
    /// 插件接口所在的 CrawlerCore 始终使用 Worker 的版本，保证 <see cref="ICrawlerPlugin"/> 类型一致。
    /// 兼容直接放在插件目录下的单个 DLL。</item>
    /// <item>一个 DLL 中有无法加载的类型时，其余插件类型照常加载；同名插件只保留按路径排序后的第一个并记错误。</item>
    /// <item>重新加载时先建好新的插件表再整体替换，期间 <see cref="GetPlugin"/> 不会返回 null。</item>
    /// </list>
    /// </summary>
    public sealed class PluginManager : IPluginManager
    {
        /// <summary>插件与 Worker 共享的程序集（插件接口与实体所在），不允许插件自带</summary>
        private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase) { "CrawlerCore" };

        private readonly object _loadLock = new();
        private readonly string _pluginDirectory;
        private volatile IReadOnlyDictionary<string, ICrawlerPlugin>? _plugins;

        public PluginManager(IOptions<CrawlerSettings> crawlerSettings)
        {
            var configured = crawlerSettings.Value.PluginDirectory;
            _pluginDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                string.IsNullOrWhiteSpace(configured) ? "Plugins" : configured));
        }

        /// <summary>实际使用的插件目录（绝对路径）</summary>
        public string PluginDirectory => _pluginDirectory;

        /// <summary>
        /// 重新扫描插件目录并加载全部插件；新的插件表建好后整体替换旧表
        /// </summary>
        public void LoadAllPlugins()
        {
            lock (_loadLock)
            {
                _plugins = LoadFromDirectory(_pluginDirectory);
            }
        }

        /// <summary>
        /// 获取插件实例（大小写不敏感）
        /// </summary>
        public ICrawlerPlugin? GetPlugin(string pluginName)
        {
            if (string.IsNullOrWhiteSpace(pluginName)) return null;
            EnsureLoaded().TryGetValue(pluginName, out var plugin);
            return plugin;
        }

        public IReadOnlyList<ICrawlerPlugin> GetAllPlugins() => EnsureLoaded().Values.ToList();

        private IReadOnlyDictionary<string, ICrawlerPlugin> EnsureLoaded()
        {
            var plugins = _plugins;
            if (plugins != null) return plugins;

            lock (_loadLock)
            {
                return _plugins ??= LoadFromDirectory(_pluginDirectory);
            }
        }

        private static IReadOnlyDictionary<string, ICrawlerPlugin> LoadFromDirectory(string directory)
        {
            var plugins = new Dictionary<string, ICrawlerPlugin>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(directory))
            {
                LogHelper.Warn($"插件目录不存在: {directory}");
                return plugins;
            }

            foreach (var dllPath in FindPluginAssemblies(directory))
            {
                foreach (var plugin in LoadPluginsFromFile(dllPath))
                {
                    if (plugins.TryAdd(plugin.Name, plugin))
                    {
                        LogHelper.Info($"插件加载成功: {plugin.Name} v{plugin.Version}（{Path.GetRelativePath(directory, dllPath)}）");
                    }
                    else
                    {
                        LogHelper.Error($"插件重名: {plugin.Name}（{dllPath}），保留先加载的 {plugins[plugin.Name].GetType().Assembly.Location}");
                    }
                }
            }

            if (plugins.Count == 0)
            {
                LogHelper.Warn($"插件目录中没有可用插件: {directory}");
            }
            else
            {
                LogHelper.Info($"插件加载完成，共 {plugins.Count} 个: {string.Join(", ", plugins.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}");
            }
            return plugins;
        }

        /// <summary>
        /// 插件程序集：先是各子目录中与目录同名的 DLL（按目录名排序），再是插件目录下直接放置的 DLL（按文件名排序）
        /// </summary>
        internal static IEnumerable<string> FindPluginAssemblies(string directory)
        {
            foreach (var subDirectory in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var mainAssembly = Path.Combine(subDirectory, Path.GetFileName(subDirectory) + ".dll");
                if (File.Exists(mainAssembly))
                {
                    yield return mainAssembly;
                }
            }

            foreach (var dllPath in Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                yield return dllPath;
            }
        }

        private static List<ICrawlerPlugin> LoadPluginsFromFile(string dllPath)
        {
            var result = new List<ICrawlerPlugin>();
            Assembly assembly;
            try
            {
                var context = new PluginLoadContext(dllPath);
                assembly = context.LoadFromAssemblyPath(dllPath);
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"插件 DLL 加载失败: {dllPath}");
                return result;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 部分类型依赖缺失：其余类型照常使用，并记录具体的加载错误
                types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
                var reasons = string.Join("; ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct());
                LogHelper.Warn($"插件 DLL 中有 {ex.LoaderExceptions.Length} 个类型无法加载（{Path.GetFileName(dllPath)}）: {reasons}");
            }
            catch (Exception ex)
            {
                LogHelper.Error(ex, $"读取插件 DLL 的类型失败: {dllPath}");
                return result;
            }

            foreach (var type in types.Where(t => typeof(ICrawlerPlugin).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }))
            {
                try
                {
                    if (Activator.CreateInstance(type) is ICrawlerPlugin plugin && !string.IsNullOrWhiteSpace(plugin.Name))
                    {
                        result.Add(plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Error(ex, $"插件实例化失败: {type.FullName}（{Path.GetFileName(dllPath)}）");
                }
            }
            return result;
        }

        /// <summary>
        /// 插件的加载上下文：插件自己的依赖从插件目录解析；共享程序集和解析不到的程序集交给默认上下文（Worker 的版本）
        /// </summary>
        private sealed class PluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver? _resolver;
            private readonly string _directory;

            public PluginLoadContext(string mainAssemblyPath)
                : base($"Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: false)
            {
                _directory = Path.GetDirectoryName(mainAssemblyPath)!;
                try
                {
                    _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"插件 {Path.GetFileName(mainAssemblyPath)} 无法创建依赖解析器，改为按插件目录查找依赖: {ex.Message}");
                }
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                if (assemblyName.Name == null || SharedAssemblyNames.Contains(assemblyName.Name))
                {
                    return null;
                }

                var path = _resolver?.ResolveAssemblyToPath(assemblyName);
                if (path == null)
                {
                    var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
                    if (File.Exists(candidate) && !IsProvidedByHost(assemblyName))
                    {
                        path = candidate;
                    }
                }
                return path != null ? LoadFromAssemblyPath(path) : null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
                return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
            }

            /// <summary>没有 deps.json 的插件（直接放在插件目录下的 DLL）不覆盖 Worker 自带的程序集</summary>
            private static bool IsProvidedByHost(AssemblyName assemblyName)
                => Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
