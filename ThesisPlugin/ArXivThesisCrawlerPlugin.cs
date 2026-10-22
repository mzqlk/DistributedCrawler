using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerCore.Infrastructure;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CrawlerPlugins.ThesisPlugin
{
    /// <summary>
    /// arXiv论文专属爬虫插件：解析 arXiv 论文详情页（/abs/）。
    /// <para>
    /// 输出的 parsedData 键（ArXivId、Title、Authors、AuthorsList、Abstract、Subjects、SubjectsList、PublicationDate、DOI、
    /// PdfUrl、SourceUrl、NormalizedUrl、Comment）是 DataAnalysis 的输入契约，键名不能改（见 CLAUDE.md）。
    /// </para>
    /// <para>
    /// 解析以页面结构为准（h1.title、div.authors 下的每个链接、blockquote.abstract、td.subjects、td.comments），
    /// 结构缺失时回退到页面给机器读取的 citation_* 元数据；不再对整页文本做正则匹配（W-05、W-06：那会把作者截成第一位、
    /// 把摘要变成链接文字或混入 Comments 行）。标题取不到时视为页面结构不匹配，解析失败，而不是输出错误数据。
    /// </para>
    /// </summary>
    public class ArXivThesisCrawlerPlugin : ICrawlerPlugin
    {
        /// <summary>
        /// 插件全局唯一名称
        /// </summary>
        public string Name => "ArXivThesisPlugin";

        /// <summary>
        /// 插件版本
        /// </summary>
        public string Version => "1.4.0";

        #region 正则表达式
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        // arXiv 编号：新式 2106.09685，旧式 hep-th/9901001；版本号单独匹配
        private static readonly Regex ArXivIdRegex = new(@"arXiv:\s*((?:\d{4}\.\d{4,5})|(?:[a-z\-]+(?:\.[A-Z]{2})?/\d{7}))(?:v\d+)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex AbsPathIdRegex = new(@"/abs/((?:\d{4}\.\d{4,5})|(?:[a-z\-]+(?:\.[A-Z]{2})?/\d{7}))(?:v\d+)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex VersionSuffixRegex = new(@"v\d+$", RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex SubmitDateRegex = new(@"Submitted on (\d{1,2}\s+\w+\s+\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex DoiRegex = new(@"https://doi\.org/([^\s<>""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled, RegexTimeout);
        #endregion

        #region URL 过滤正则（用于作用域限制）
        // 只允许论文详情页：https://arxiv.org/abs/数字.数字[版本号]
        private static readonly Regex AbsUrlPattern = new(
            @"^https?://arxiv\.org/abs/\d+\.\d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout
        );

        // robots.txt 中明确禁止的路径模式（快速拒绝）。是否允许请求以引擎的 robots 策略为准，这里只是插件自己的作用域限制
        private static readonly Regex DisallowedPathPattern = new(
            @"/(IgnoreMe|search|auth|user|form|find|cookies|api|prevnext|trackback|src|ps|dvi|format|PS_cache|Stats|oai2|tb|ct|show-email)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout
        );
        #endregion

        /// <summary>
        /// 插件作用域过滤：只允许爬取符合规范的 arXiv 论文详情页
        /// </summary>
        public bool ShouldFollowLink(string url, int currentDepth)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            // 1. 快速拒绝 robots.txt 明确禁止的路径
            if (DisallowedPathPattern.IsMatch(url))
                return false;

            // 2. 只允许 /abs/ 论文详情页
            return AbsUrlPattern.IsMatch(url);
        }

        /// <summary>
        /// 执行arXiv论文页面解析核心逻辑
        /// </summary>
        public Task<PluginResult> ExecuteAsync(PluginContext context)
        {
            var result = new PluginResult();
            try
            {
                LogHelper.Debug($"[{Name}] 开始解析: {context.Url}");

                #region 前置校验与 URL 规范化
                if (string.IsNullOrWhiteSpace(context.Url))
                {
                    return Task.FromResult(Fail(result, "目标URL为空，无法执行解析"));
                }
                if (string.IsNullOrWhiteSpace(context.Html))
                {
                    return Task.FromResult(Fail(result, "HTML内容为空，可能是页面加载失败或反爬限制"));
                }

                var uri = new Uri(context.Url);
                var normalizedUrl = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
                var path = uri.AbsolutePath.ToLowerInvariant();

                // 下载后的路径校验（防御性）。请求前是否允许访问由引擎的 robots 策略负责
                if (!path.StartsWith("/abs/", StringComparison.Ordinal))
                {
                    return Task.FromResult(Fail(result, "插件仅支持arXiv论文详情页(/abs/路径)"));
                }
                #endregion

                #region HTML 解析与字段提取
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(context.Html);
                var root = htmlDoc.DocumentNode;

                // 1. ArXivId（不带版本号）：citation_arxiv_id → “Cite as” 中的编号 → URL 路径
                var arxivId = ExtractArXivId(root, uri);
                result.Data["ArXivId"] = arxivId;

                // 2. 标题：h1.title 去掉 “Title:” 标签 → citation_title
                var title = TextWithoutDescriptor(root.SelectSingleNode("//h1[contains(concat(' ', normalize-space(@class), ' '), ' title ')]"));
                if (string.IsNullOrEmpty(title))
                {
                    title = MetaContents(root, "citation_title").FirstOrDefault() ?? string.Empty;
                }
                result.Data["Title"] = title;

                // 3. 作者：div.authors 下的每个链接（“名 姓”）→ citation_author（“姓, 名”，转换为“名 姓”）
                var authors = root.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' authors ')]/a")
                    ?.Select(a => CleanText(a.InnerText))
                    .Where(a => a.Length > 0)
                    .ToList() ?? new List<string>();
                if (authors.Count == 0)
                {
                    authors = MetaContents(root, "citation_author").Select(ToGivenNameFirst).Where(a => a.Length > 0).ToList();
                }
                result.Data["Authors"] = string.Join(", ", authors);
                result.Data["AuthorsList"] = authors;

                // 4. 摘要：blockquote.abstract 去掉 “Abstract:” 标签，保留其中链接的文字 → citation_abstract
                var abstractContent = TextWithoutDescriptor(root.SelectSingleNode("//blockquote[contains(concat(' ', normalize-space(@class), ' '), ' abstract ')]"));
                if (string.IsNullOrEmpty(abstractContent))
                {
                    abstractContent = MetaContents(root, "citation_abstract").FirstOrDefault() ?? string.Empty;
                }
                result.Data["Abstract"] = abstractContent;

                // 5. 主题分类：td.subjects → “Subjects:” 所在行的第二列
                var subjectNode = root.SelectSingleNode("//td[contains(concat(' ', normalize-space(@class), ' '), ' subjects ')]")
                    ?? root.SelectSingleNode("//tr[td[contains(text(),'Subjects:')]]/td[2]");
                var subjects = subjectNode == null
                    ? new List<string>()
                    : CleanText(subjectNode.InnerText).Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(CleanText)
                        .Where(s => s.Length > 0)
                        .ToList();
                result.Data["Subjects"] = string.Join("; ", subjects);
                result.Data["SubjectsList"] = subjects;

                // 6. 首次提交日期（“12 Jun 2017” 格式，与历史数据一致）：div.dateline → citation_date
                result.Data["PublicationDate"] = ExtractPublicationDate(root);

                // 7. DOI：页面上的第一个 doi.org 链接（保持原有口径，见 docs/review/worker.md W-29）
                var doiMatch = DoiRegex.Match(root.InnerHtml);
                result.Data["DOI"] = doiMatch.Success ? doiMatch.Groups[1].Value.Trim() : string.Empty;

                // 8. PDF 链接、来源与备注
                result.Data["PdfUrl"] = string.IsNullOrEmpty(arxivId) ? string.Empty : $"https://arxiv.org/pdf/{arxivId}.pdf";
                result.Data["SourceUrl"] = context.Url;
                result.Data["NormalizedUrl"] = normalizedUrl;
                result.Data["Comment"] = CleanText(root.SelectSingleNode("//td[contains(concat(' ', normalize-space(@class), ' '), ' comments ')]")?.InnerText);
                #endregion

                #region 结果校验
                if (string.IsNullOrEmpty(title))
                {
                    return Task.FromResult(Fail(result, "未提取到论文标题，页面结构不匹配"));
                }

                result.Success = true;
                LogHelper.Debug($"[{Name}] 解析成功: {arxivId}，作者 {authors.Count} 位，摘要 {abstractContent.Length} 字符");
                #endregion

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"解析异常: {ex.Message}";
                LogHelper.Error(ex, $"[{Name}] 解析发生异常，URL: {context.Url}");
                return Task.FromResult(result);
            }
        }

        public Dictionary<string, string> GetDefaultConfiguration()
        {
            return new Dictionary<string, string>
            {
                ["CrawlDelay"] = "15000",
                ["Description"] = "arXiv论文专属爬虫插件，精准适配/abs/路径页面结构"
            };
        }

        #region 辅助方法
        private PluginResult Fail(PluginResult result, string message)
        {
            result.Success = false;
            result.ErrorMessage = $"解析失败：{message}";
            LogHelper.Warn($"[{Name}] {result.ErrorMessage}");
            return result;
        }

        /// <summary>折叠空白并解码 HTML 实体</summary>
        private static string CleanText(string? rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return string.Empty;
            return WhitespaceRegex.Replace(HtmlEntity.DeEntitize(rawText), " ").Trim();
        }

        /// <summary>节点的文本，去掉其中的 span.descriptor（“Title:”“Abstract:” 等标签）</summary>
        private static string TextWithoutDescriptor(HtmlNode? node)
        {
            if (node == null) return string.Empty;
            var clone = node.CloneNode(deep: true);
            var descriptors = clone.SelectNodes(".//span[contains(concat(' ', normalize-space(@class), ' '), ' descriptor ')]");
            if (descriptors != null)
            {
                foreach (var descriptor in descriptors)
                {
                    descriptor.Remove();
                }
            }
            return CleanText(clone.InnerText);
        }

        private static IEnumerable<string> MetaContents(HtmlNode root, string name)
            => root.SelectNodes($"//meta[@name='{name}']")
                   ?.Select(m => CleanText(m.GetAttributeValue("content", string.Empty)))
                   .Where(c => c.Length > 0)
               ?? Enumerable.Empty<string>();

        /// <summary>citation_author 的 “姓, 名” 转为页面显示的 “名 姓”</summary>
        private static string ToGivenNameFirst(string citationAuthor)
        {
            var parts = citationAuthor.Split(',', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 && parts[1].Length > 0 ? $"{parts[1]} {parts[0]}" : citationAuthor;
        }

        private static string ExtractArXivId(HtmlNode root, Uri uri)
        {
            var fromMeta = MetaContents(root, "citation_arxiv_id").FirstOrDefault();
            if (!string.IsNullOrEmpty(fromMeta))
            {
                return VersionSuffixRegex.Replace(fromMeta, string.Empty);
            }

            var citeAs = root.SelectSingleNode("//span[contains(concat(' ', normalize-space(@class), ' '), ' arxivid ')]");
            var match = citeAs == null ? Match.Empty : ArXivIdRegex.Match(CleanText(citeAs.InnerText));
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            match = AbsPathIdRegex.Match(uri.AbsolutePath);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ExtractPublicationDate(HtmlNode root)
        {
            var dateline = root.SelectSingleNode("//div[contains(concat(' ', normalize-space(@class), ' '), ' dateline ')]");
            var match = dateline == null ? Match.Empty : SubmitDateRegex.Match(CleanText(dateline.InnerText));
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            // citation_date 形如 2017/06/12
            var citationDate = MetaContents(root, "citation_date").FirstOrDefault();
            return citationDate != null
                   && DateTime.TryParseExact(citationDate, "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : string.Empty;
        }
        #endregion
    }
}
