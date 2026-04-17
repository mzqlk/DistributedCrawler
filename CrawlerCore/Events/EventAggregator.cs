using System;
using System.Collections.Concurrent;

namespace CrawlerCore.Events
{
    /// <summary>
    /// 全局事件聚合器实现
    /// </summary>
    public class EventAggregator : IEventAggregator
    {
        // 按事件类型存储「订阅ID → 处理程序」的映射
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Delegate>> _handlers = new();

        public Guid Subscribe<T>(EventHandler<T> handler) where T : EventArgs
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            var eventType = typeof(T);
            var subscriptionId = Guid.NewGuid();

            // 获取或创建该事件类型的订阅字典
            var eventHandlers = _handlers.GetOrAdd(eventType, _ => new ConcurrentDictionary<Guid, Delegate>());

            // 添加订阅
            eventHandlers.TryAdd(subscriptionId, handler);

            return subscriptionId;
        }

        public void Unsubscribe<T>(Guid subscriptionId) where T : EventArgs
        {
            if (subscriptionId == Guid.Empty) throw new ArgumentException("订阅ID不能为空", nameof(subscriptionId));

            var eventType = typeof(T);
            if (_handlers.TryGetValue(eventType, out var eventHandlers))
            {
                // 直接移除订阅，无需过滤重建
                eventHandlers.TryRemove(subscriptionId, out _);
            }
        }

        public void Publish<T>(object sender, T args) where T : EventArgs
        {
            if (args == null) throw new ArgumentNullException(nameof(args));

            var eventType = typeof(T);
            if (_handlers.TryGetValue(eventType, out var eventHandlers))
            {
                foreach (var handler in eventHandlers.Values)
                {
                    try
                    {
                        ((EventHandler<T>)handler)(sender, args);
                    }
                    catch (Exception ex)
                    {
                        // 单个处理程序异常不影响其他处理程序
                        // Core层不依赖外部日志库，仅输出到Debug
                        System.Diagnostics.Debug.WriteLine($"[{nameof(EventAggregator)}] Event handler failed: {ex}");
                    }
                }
            }
        }
    }
}