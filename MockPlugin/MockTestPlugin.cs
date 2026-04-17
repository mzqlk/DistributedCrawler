using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CrawlerPlugins.MockPlugin
{
    /// <summary>
    /// 测试模拟插件（用于单元测试）
    /// </summary>
    public class MockTestPlugin : ICrawlerPlugin
    {
        public string Name => "MockTestPlugin";
        public string Version => "1.0.0";

        public async Task<PluginResult> ExecuteAsync(PluginContext context)
        {
            await Task.CompletedTask;
            return new PluginResult
            {
                Success = true,
                Data = new Dictionary<string, object>
                {
                    ["Url"] = context.Url,
                    ["Timestamp"] = DateTime.UtcNow
                }
            };
        }

        public Dictionary<string, string> GetDefaultConfiguration()
        {
            return new Dictionary<string, string>
            {
                ["RequestTimeout"] = "3000",
                ["MaxRetry"] = "3"
            };
        }
    }
}
