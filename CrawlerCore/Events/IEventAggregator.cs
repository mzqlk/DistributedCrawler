using System;

namespace CrawlerCore.Events
{
    /// <summary>
    /// 全局事件聚合器接口（Singleton，跨服务/跨Scope，保证事件有序）
    /// </summary>
    public interface IEventAggregator
    {
        /// <summary>
        /// 订阅事件
        /// </summary>
        /// <typeparam name="T">事件参数类型</typeparam>
        /// <param name="handler">事件处理程序</param>
        /// <returns>订阅唯一标识（用于取消订阅）</returns>
        Guid Subscribe<T>(EventHandler<T> handler) where T : EventArgs;

        /// <summary>
        /// 取消订阅事件
        /// </summary>
        /// <typeparam name="T">事件参数类型</typeparam>
        /// <param name="subscriptionId">订阅唯一标识</param>
        void Unsubscribe<T>(Guid subscriptionId) where T : EventArgs;

        /// <summary>
        /// 发布事件（同步，保证事件按发布顺序传递）
        /// </summary>
        /// <typeparam name="T">事件参数类型</typeparam>
        /// <param name="sender">事件发布者</param>
        /// <param name="args">事件参数</param>
        void Publish<T>(object sender, T args) where T : EventArgs;
    }
}