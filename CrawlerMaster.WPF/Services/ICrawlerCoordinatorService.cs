using System.Threading.Tasks;
using CrawlerCore.Entities;

namespace CrawlerMaster.WPF.Services
{
    public interface ICrawlerCoordinatorService
    {
        Task<bool> SendTaskToNodeAsync(string nodeId, CrawlerTask task);
    }
}