using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CrawlerWorker.Services.Engine.Robots
{
    /// <summary>
    /// robots.txt 的解析结果（按 RFC 9309）：
    /// <list type="bullet">
    /// <item>连续的 User-agent 行开始一个规则组；产品名（不区分大小写）与本程序一致的组优先，没有时用 “*” 组，同名的多个组合并；</item>
    /// <item>Allow/Disallow 的路径支持 “*”（任意字符）与结尾的 “$”（结尾锚定），与 URL 的路径加查询串匹配；</item>
    /// <item>匹配最长的规则生效，长度相同时 Allow 优先；空的 Disallow 不构成规则；/robots.txt 本身总是允许；</item>
    /// <item>Crawl-delay 不是 RFC 的一部分，但 arXiv 等网站在用，取所选规则组中的最大值。</item>
    /// </list>
    /// </summary>
    public sealed class RobotsTxt
    {
        /// <summary>RFC 9309 要求至少解析 500 KiB，超出部分忽略</summary>
        public const int MaxParsedBytes = 500 * 1024;

        private readonly List<Rule> _rules;

        private RobotsTxt(List<Rule> rules, TimeSpan crawlDelay)
        {
            _rules = rules;
            CrawlDelay = crawlDelay;
        }

        /// <summary>允许访问一切（robots.txt 不存在，或返回 4xx）</summary>
        public static RobotsTxt AllowAll { get; } = new(new List<Rule>(), TimeSpan.Zero);

        /// <summary>所选规则组的 Crawl-delay；没有时为 0</summary>
        public TimeSpan CrawlDelay { get; }

        /// <summary>所选规则组中的规则数</summary>
        public int RuleCount => _rules.Count;

        /// <summary>
        /// 解析 robots.txt 内容，选出适用于 <paramref name="productToken"/>（例如 DistributedCrawler）的规则组
        /// </summary>
        public static RobotsTxt Parse(string? content, string productToken)
        {
            if (string.IsNullOrEmpty(content))
            {
                return AllowAll;
            }

            var groups = new List<Group>();
            Group? current = null;
            var lastLineWasUserAgent = false;

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine;
                var comment = line.IndexOf('#');
                if (comment >= 0) line = line[..comment];
                line = line.Trim();
                if (line.Length == 0) continue;

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();

                switch (key)
                {
                    case "user-agent":
                        if (current == null || !lastLineWasUserAgent)
                        {
                            current = new Group();
                            groups.Add(current);
                        }
                        current.Agents.Add(value);
                        lastLineWasUserAgent = true;
                        continue;
                    case "allow":
                    case "disallow":
                        if (current != null && value.Length > 0)
                        {
                            current.Rules.Add(new Rule(value, key == "allow"));
                        }
                        break;
                    case "crawl-delay":
                        if (current != null
                            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                            && seconds > 0 && seconds < TimeSpan.FromDays(1).TotalSeconds)
                        {
                            current.CrawlDelay = Max(current.CrawlDelay, TimeSpan.FromSeconds(seconds));
                        }
                        break;
                }
                lastLineWasUserAgent = false;
            }

            var selected = groups.Where(g => g.Agents.Any(a => string.Equals(a, productToken, StringComparison.OrdinalIgnoreCase))).ToList();
            if (selected.Count == 0)
            {
                selected = groups.Where(g => g.Agents.Any(a => a == "*")).ToList();
            }
            if (selected.Count == 0)
            {
                return AllowAll;
            }

            return new RobotsTxt(
                selected.SelectMany(g => g.Rules).ToList(),
                selected.Aggregate(TimeSpan.Zero, (delay, g) => Max(delay, g.CrawlDelay)));
        }

        /// <summary>判断 URL 是否允许访问</summary>
        public bool IsAllowed(Uri url)
        {
            var target = url.PathAndQuery;
            if (string.IsNullOrEmpty(target)) target = "/";
            if (string.Equals(url.AbsolutePath, "/robots.txt", StringComparison.OrdinalIgnoreCase)) return true;

            Rule? best = null;
            foreach (var rule in _rules)
            {
                if (!rule.Matches(target)) continue;
                if (best == null
                    || rule.Pattern.Length > best.Pattern.Length
                    || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
                {
                    best = rule;
                }
            }
            return best?.Allow ?? true;
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private sealed class Group
        {
            public List<string> Agents { get; } = new();
            public List<Rule> Rules { get; } = new();
            public TimeSpan CrawlDelay { get; set; }
        }

        private sealed class Rule
        {
            public Rule(string pattern, bool allow)
            {
                Pattern = pattern;
                Allow = allow;
            }

            public string Pattern { get; }
            public bool Allow { get; }

            /// <summary>从路径开头匹配；“*” 匹配任意字符序列，结尾的 “$” 要求匹配到路径末尾</summary>
            public bool Matches(string path)
            {
                var anchored = Pattern.EndsWith('$');
                var pattern = anchored ? Pattern[..^1] : Pattern;
                return Match(pattern, 0, path, 0, anchored);
            }

            private static bool Match(string pattern, int pi, string path, int si, bool anchored)
            {
                while (pi < pattern.Length)
                {
                    if (pattern[pi] == '*')
                    {
                        // 连续的 * 等价于一个
                        while (pi < pattern.Length && pattern[pi] == '*') pi++;
                        if (pi == pattern.Length) return true;
                        for (var start = si; start <= path.Length; start++)
                        {
                            if (Match(pattern, pi, path, start, anchored)) return true;
                        }
                        return false;
                    }
                    if (si >= path.Length || pattern[pi] != path[si]) return false;
                    pi++;
                    si++;
                }
                return !anchored || si == path.Length;
            }
        }
    }
}
