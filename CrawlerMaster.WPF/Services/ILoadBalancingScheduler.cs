using CrawlerCore.Entities;
using System.Collections.Generic;

namespace CrawlerMaster.WPF.Services
{
    public interface ILoadBalancingScheduler
    {
        NodeInfo SelectOptimalNode(IEnumerable<NodeInfo> availableNodes, CrawlerTask task);
    }
}