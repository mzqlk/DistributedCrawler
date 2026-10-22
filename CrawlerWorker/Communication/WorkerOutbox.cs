using CrawlerCore.Infrastructure;
using CrawlerCore.Protos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CrawlerWorker.Communication
{
    /// <summary>
    /// 进程级发件箱（W-03）：Worker 发往 Master 的所有消息都经过它，由当前双向流的单一写循环依次写出。
    /// <list type="bullet">
    /// <item>通道在进程内只创建一次，跨越断线重连；写入失败的那条消息保留下来，下一条流先发它。</item>
    /// <item>心跳与运行中的进度是“即时消息”：只在有流时入队，断线期间直接丢弃（过时的心跳和进度没有意义）。</item>
    /// <item>任务的最终结果（结果 + Completed 进度，或 Failed 进度）是“可靠消息”，按 TaskId 保存在待确认表中：
    /// 需要回执的（结果）直到收到 Master 的 <see cref="ResultAck"/> 才删除；新流建立后、或写出后超过回执超时，都会重发。
    /// 不需要回执的（Failed）全部写出后删除。重复发送由 Master 按 TaskId 去重。</item>
    /// </list>
    /// </summary>
    public sealed class WorkerOutbox
    {
        private static readonly TimeSpan MaxResendInterval = TimeSpan.FromMinutes(10);

        private readonly Channel<OutboxItem> _channel = Channel.CreateUnbounded<OutboxItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        private readonly ConcurrentDictionary<string, PendingResult> _pending = new();
        private readonly object _retryLock = new();
        private readonly TimeProvider _timeProvider;
        private OutboxItem? _retryItem;
        private volatile bool _connected;
        private long _sequence;

        public WorkerOutbox(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        /// <summary>当前是否有已被 Master 接受的双向流</summary>
        public bool IsConnected => _connected;

        /// <summary>待确认（或待写出）的最终结果数</summary>
        public int PendingResultCount => _pending.Count;

        /// <summary>是否有该任务待确认的最终结果</summary>
        public bool HasPendingResult(string taskId) => _pending.ContainsKey(taskId);

        /// <summary>即时消息（心跳、运行中的进度）：有流时入队，否则丢弃并返回 false</summary>
        public bool TrySendTransient(FromWorker message)
        {
            if (!_connected) return false;
            return _channel.Writer.TryWrite(new OutboxItem(message, null, 0));
        }

        /// <summary>
        /// 任务的最终结果：按顺序写出 <paramref name="messages"/>；<paramref name="requiresAck"/> 为 true 时保留到收到回执
        /// </summary>
        public void EnqueueResult(string taskId, IReadOnlyList<FromWorker> messages, bool requiresAck)
        {
            var entry = new PendingResult(taskId, messages, requiresAck, Interlocked.Increment(ref _sequence));
            if (_pending.TryGetValue(taskId, out var previous))
            {
                previous.Removed = true;
            }
            _pending[taskId] = entry;
            Queue(entry);
        }

        /// <summary>
        /// Master 重复下发已执行完的任务时（W-23）：若有待确认的结果，尽快重发并返回 true
        /// </summary>
        public bool TryResendResult(string taskId)
        {
            if (!_pending.TryGetValue(taskId, out var entry)) return false;
            lock (entry)
            {
                if (entry.WrittenCount == 0 && !entry.FullyWritten)
                {
                    return true; // 还在队列中，马上就会发出
                }
            }
            Queue(entry);
            return true;
        }

        /// <summary>收到结果回执：无论 Master 接受还是丢弃（任务已改派），都不再重发</summary>
        public bool Acknowledge(string taskId)
        {
            if (_pending.TryRemove(taskId, out var entry))
            {
                entry.Removed = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 新的双向流已被 Master 接受：标记为已连接，并重发此前已写出（全部或部分）但没有收到回执的结果，
        /// 这些消息可能随断开的连接丢失了
        /// </summary>
        public void OnStreamAccepted()
        {
            _connected = true;
            var resent = 0;
            foreach (var entry in _pending.Values.OrderBy(e => e.Sequence))
            {
                bool needsResend;
                lock (entry)
                {
                    needsResend = entry.WrittenCount > 0 || entry.FullyWritten;
                }
                if (needsResend)
                {
                    Queue(entry);
                    resent++;
                }
            }
            if (resent > 0)
            {
                LogHelper.Info($"双向流已建立，重发 {resent} 个未确认的任务结果");
            }
        }

        /// <summary>双向流已结束</summary>
        public void OnStreamClosed() => _connected = false;

        /// <summary>
        /// 写出后超过回执超时仍未确认的结果重新入队（每次重发后超时翻倍，上限 10 分钟）。由心跳循环定期调用
        /// </summary>
        public int ResendUnacknowledged(TimeSpan ackTimeout)
        {
            if (!_connected) return 0;
            var now = _timeProvider.GetUtcNow();
            var resent = 0;
            foreach (var entry in _pending.Values.OrderBy(e => e.Sequence))
            {
                bool due;
                lock (entry)
                {
                    var wait = TimeSpan.FromTicks(Math.Min(MaxResendInterval.Ticks, ackTimeout.Ticks * (1L << Math.Min(entry.ResendCount, 10))));
                    due = entry.RequiresAck && entry.FullyWritten && now - entry.WrittenAt >= wait;
                    if (due) entry.ResendCount++;
                }
                if (due)
                {
                    LogHelper.Warn($"任务 {entry.TaskId} 的结果超过 {ackTimeout.TotalSeconds:0} 秒未收到回执，重发（第 {entry.ResendCount} 次）");
                    Queue(entry);
                    resent++;
                }
            }
            return resent;
        }

        /// <summary>
        /// 写循环读取下一条消息：先发上一条流写入失败的消息，再读通道；已确认或已被重新入队取代的消息跳过。
        /// 发件箱被 <see cref="Complete"/> 且已读完时返回 null
        /// </summary>
        public async ValueTask<OutboxItem?> ReadAsync(CancellationToken cancellationToken)
        {
            lock (_retryLock)
            {
                if (_retryItem is { } retry)
                {
                    _retryItem = null;
                    if (!retry.IsStale) return retry;
                }
            }

            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    if (!item.IsStale) return item;
                }
            }
            return null;
        }

        /// <summary>消息已写入流</summary>
        public void OnWritten(OutboxItem item)
        {
            var entry = item.Entry;
            if (entry == null) return;

            var completed = false;
            lock (entry)
            {
                if (item.Generation != entry.Generation) return;
                entry.WrittenCount++;
                if (entry.WrittenCount >= entry.Messages.Count)
                {
                    entry.WrittenCount = 0;
                    entry.FullyWritten = true;
                    entry.WrittenAt = _timeProvider.GetUtcNow();
                    completed = true;
                }
            }

            if (completed && !entry.RequiresAck)
            {
                // 不需要回执的最终状态（Failed）写出即完成
                _pending.TryRemove(new KeyValuePair<string, PendingResult>(entry.TaskId, entry));
                entry.Removed = true;
            }
        }

        /// <summary>写入失败：可靠消息留给下一条流先发；即时消息丢弃</summary>
        public void OnWriteFailed(OutboxItem item)
        {
            if (item.Entry == null) return;
            lock (_retryLock)
            {
                _retryItem = item;
            }
        }

        /// <summary>停止时等待需要回执的结果全部确认，超时或取消时返回 false</summary>
        public async Task<bool> WaitForAcknowledgementsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = _timeProvider.GetUtcNow() + timeout;
            while (_pending.Values.Any(e => e.RequiresAck))
            {
                if (!_connected || _timeProvider.GetUtcNow() >= deadline || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>停止时调用：不再接受新消息，写循环写完已有消息后结束</summary>
        public void Complete() => _channel.Writer.TryComplete();

        private void Queue(PendingResult entry)
        {
            int generation;
            lock (entry)
            {
                if (entry.Removed) return;
                entry.Generation++;
                entry.WrittenCount = 0;
                entry.FullyWritten = false;
                generation = entry.Generation;
            }
            foreach (var message in entry.Messages)
            {
                _channel.Writer.TryWrite(new OutboxItem(message, entry, generation));
            }
        }

        /// <summary>发件箱中的一条消息</summary>
        public sealed class OutboxItem
        {
            internal OutboxItem(FromWorker message, PendingResult? entry, int generation)
            {
                Message = message;
                Entry = entry;
                Generation = generation;
            }

            public FromWorker Message { get; }

            /// <summary>所属的最终结果；即时消息为 null</summary>
            internal PendingResult? Entry { get; }

            internal int Generation { get; }

            /// <summary>所属结果已确认，或已被重新入队（新一代消息取代了这一条）</summary>
            internal bool IsStale
            {
                get
                {
                    if (Entry == null) return false;
                    lock (Entry)
                    {
                        return Entry.Removed || Generation != Entry.Generation;
                    }
                }
            }
        }

        /// <summary>一个任务待确认的最终结果</summary>
        internal sealed class PendingResult
        {
            public PendingResult(string taskId, IReadOnlyList<FromWorker> messages, bool requiresAck, long sequence)
            {
                TaskId = taskId;
                Messages = messages;
                RequiresAck = requiresAck;
                Sequence = sequence;
            }

            public string TaskId { get; }
            public IReadOnlyList<FromWorker> Messages { get; }
            public bool RequiresAck { get; }
            public long Sequence { get; }

            // 以下字段在 lock (this) 中读写
            public int Generation { get; set; }
            public int WrittenCount { get; set; }
            public bool FullyWritten { get; set; }
            public DateTimeOffset WrittenAt { get; set; }
            public int ResendCount { get; set; }
            public volatile bool Removed;
        }
    }
}
