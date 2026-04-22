using CrawlerCore.Entities;
using CrawlerCore.Interfaces;
using CrawlerCore.Infrastructure;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CrawlerPlugins.ThesisPlugin
{
    /// <summary>
    /// arXiv论文专属爬虫插件
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
        public string Version => "1.3.0";

        #region 正则表达式（预编译，用于内容提取）
        private static readonly Regex ArXivIdRegex = new Regex(@"arXiv:([\d]+\.[\d]+v?[\d]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SubmitDateRegex = new Regex(@"Submitted on (\d{1,2}\s+\w+\s+\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DoiRegex = new Regex(@"https://doi\.org/([^\s<>""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        #endregion

        #region URL 过滤正则（用于作用域限制）
        // 只允许论文详情页：https://arxiv.org/abs/数字.数字[版本号]
        private static readonly Regex AbsUrlPattern = new(
            @"^https?://arxiv\.org/abs/\d+\.\d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        // robots.txt 中明确禁止的路径模式（快速拒绝）
        private static readonly Regex DisallowedPathPattern = new(
            @"/(IgnoreMe|search|auth|user|form|find|cookies|api|prevnext|trackback|src|ps|dvi|format|PS_cache|Stats|oai2|tb|ct|show-email)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
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
        public async Task<PluginResult> ExecuteAsync(PluginContext context)
        {
            var result = new PluginResult();
            try
            {
                LogHelper.Info($"[{Name}] 开始执行解析，目标URL: {context.Url}");

                #region 前置校验与 URL 规范化
                if (string.IsNullOrWhiteSpace(context.Url))
                {
                    result.Success = false;
                    result.ErrorMessage = "目标URL为空，无法执行解析";
                    LogHelper.Warn($"[{Name}] 解析终止：{result.ErrorMessage}");
                    return result;
                }
                if (string.IsNullOrWhiteSpace(context.Html))
                {
                    result.Success = false;
                    result.ErrorMessage = "HTML内容为空，可能是页面加载失败或反爬限制";
                    LogHelper.Warn($"[{Name}] 解析终止：{result.ErrorMessage}");
                    return result;
                }

                var uri = new Uri(context.Url);
                var normalizedUrl = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
                var urlLower = normalizedUrl.ToLower();

                // robots 协议合规检查
                var allowedPaths = new[] { "/abs/" };
                var disallowedPaths = new[] { "/search", "/api", "/user", "/form", "/find", "/auth", "/pdf/" };
                if (disallowedPaths.Any(p => urlLower.Contains(p)))
                {
                    result.Success = false;
                    result.ErrorMessage = "解析失败：该路径为arXiv robots协议禁止爬取的类型";
                    LogHelper.Error($"[{Name}] 解析终止：{result.ErrorMessage}，URL: {context.Url}");
                    return result;
                }
                if (!allowedPaths.Any(p => urlLower.Contains(p)))
                {
                    result.Success = false;
                    result.ErrorMessage = "解析失败：插件仅支持arXiv论文详情页(/abs/路径)";
                    LogHelper.Warn($"[{Name}] 解析终止：{result.ErrorMessage}，URL: {context.Url}");
                    return result;
                }
                #endregion

                #region HTML 解析与字段提取
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(context.Html);
                var fullHtmlText = htmlDoc.DocumentNode.InnerText;

                // 1. 提取 ArXivId
                string arxivId = string.Empty;
                var arxivIdMatch = ArXivIdRegex.Match(fullHtmlText);
                if (arxivIdMatch.Success)
                {
                    arxivId = arxivIdMatch.Groups[1].Value.Trim();
                }
                result.Data["ArXivId"] = arxivId;
                LogHelper.Debug($"[{Name}] 提取ArXivId: {arxivId}");

                // 2. 提取标题
                string title = string.Empty;
                var titleNode = htmlDoc.DocumentNode.SelectSingleNode("//*[contains(text(),'Title:')]/following-sibling::*[1]")
                    ?? htmlDoc.DocumentNode.SelectSingleNode("//*[starts-with(normalize-space(text()),'Title:')]");
                if (titleNode != null)
                {
                    title = CleanText(titleNode.InnerText).Replace("Title:", "", StringComparison.OrdinalIgnoreCase).Trim();
                }
                if (string.IsNullOrEmpty(title))
                {
                    var titleMatch = Regex.Match(fullHtmlText, @"Title:\s*([\s\S]*?)(?=Authors:|Abstract:|$)", RegexOptions.IgnoreCase);
                    if (titleMatch.Success) title = CleanText(titleMatch.Groups[1].Value);
                }
                result.Data["Title"] = title;
                LogHelper.Debug($"[{Name}] 提取标题: {title}");

                // 3. 提取作者
                var authors = new List<string>();
                var authorNode = htmlDoc.DocumentNode.SelectSingleNode("//*[contains(text(),'Authors:')]/following-sibling::*[1]")
                    ?? htmlDoc.DocumentNode.SelectSingleNode("//*[starts-with(normalize-space(text()),'Authors:')]");
                if (authorNode != null)
                {
                    var authorText = CleanText(authorNode.InnerText).Replace("Authors:", "", StringComparison.OrdinalIgnoreCase).Trim();
                    authors = authorText.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(a => CleanText(a))
                                        .Where(a => !string.IsNullOrEmpty(a))
                                        .ToList();
                }
                if (authors.Count == 0)
                {
                    var authorMatch = Regex.Match(fullHtmlText, @"Authors:\s*([\s\S]*?)(?=Abstract:|Title:|$)", RegexOptions.IgnoreCase);
                    if (authorMatch.Success)
                    {
                        var authorText = CleanText(authorMatch.Groups[1].Value);
                        authors = authorText.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(a => CleanText(a))
                                            .Where(a => !string.IsNullOrEmpty(a))
                                            .ToList();
                    }
                }
                result.Data["Authors"] = string.Join(", ", authors);
                result.Data["AuthorsList"] = authors;
                LogHelper.Debug($"[{Name}] 提取作者: {string.Join(", ", authors)}");

                // 4. 提取摘要
                string abstractContent = string.Empty;
                var abstractNode = htmlDoc.DocumentNode.SelectSingleNode("//*[contains(text(),'Abstract:')]/following-sibling::*[1]")
                    ?? htmlDoc.DocumentNode.SelectSingleNode("//*[starts-with(normalize-space(text()),'Abstract:')]");
                if (abstractNode != null)
                {
                    abstractContent = CleanText(abstractNode.InnerText).Replace("Abstract:", "", StringComparison.OrdinalIgnoreCase).Trim();
                }
                if (string.IsNullOrEmpty(abstractContent))
                {
                    var abstractMatch = Regex.Match(fullHtmlText, @"Abstract:\s*([\s\S]*?)(?=Subjects:|$)", RegexOptions.IgnoreCase);
                    if (abstractMatch.Success) abstractContent = CleanText(abstractMatch.Groups[1].Value);
                }
                result.Data["Abstract"] = abstractContent;
                LogHelper.Debug($"[{Name}] 提取摘要完成，长度: {abstractContent.Length}");

                // 5. 提取主题分类
                var subjects = new List<string>();
                var subjectNode = htmlDoc.DocumentNode.SelectSingleNode("//tr[td[contains(text(),'Subjects:')]]/td[2]");
                if (subjectNode != null)
                {
                    var subjectText = CleanText(subjectNode.InnerText);
                    subjects = subjectText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(s => CleanText(s))
                                          .Where(s => !string.IsNullOrEmpty(s))
                                          .ToList();
                }
                result.Data["Subjects"] = string.Join("; ", subjects);
                result.Data["SubjectsList"] = subjects;
                LogHelper.Debug($"[{Name}] 提取分类: {string.Join("; ", subjects)}");

                // 6. 提取提交时间
                string publicationDate = string.Empty;
                var dateMatch = SubmitDateRegex.Match(fullHtmlText);
                if (dateMatch.Success)
                {
                    publicationDate = dateMatch.Groups[1].Value.Trim();
                }
                result.Data["PublicationDate"] = publicationDate;
                LogHelper.Debug($"[{Name}] 提取提交时间: {publicationDate}");

                // 7. 提取 DOI
                string doi = string.Empty;
                var doiMatch = DoiRegex.Match(htmlDoc.DocumentNode.InnerHtml);
                if (doiMatch.Success)
                {
                    doi = doiMatch.Groups[1].Value.Trim();
                }
                result.Data["DOI"] = doi;
                LogHelper.Debug($"[{Name}] 提取DOI: {doi}");

                // 8. 生成 PDF 链接等
                string pdfUrl = string.IsNullOrEmpty(arxivId) ? string.Empty : $"https://arxiv.org/pdf/{arxivId}.pdf";
                result.Data["PdfUrl"] = pdfUrl;
                result.Data["SourceUrl"] = context.Url;
                result.Data["NormalizedUrl"] = normalizedUrl;
                result.Data["Comment"] = string.Empty;
                #endregion

                #region 结果校验
                if (string.IsNullOrEmpty(arxivId) && string.IsNullOrEmpty(title))
                {
                    result.Success = false;
                    result.ErrorMessage = "解析失败：未提取到论文核心信息，页面结构不匹配";
                    LogHelper.Error($"[{Name}] 解析失败：{result.ErrorMessage}，URL: {context.Url}");
                    return result;
                }

                result.Success = true;
                LogHelper.Info($"[{Name}] 论文解析成功！arXiv编号: {arxivId}，标题: {title}");
                #endregion

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"解析异常: {ex.Message}";
                LogHelper.Error($"[{Name}] 解析发生异常，URL: {context.Url}");
                return result;
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
        private string CleanText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return string.Empty;
            return Regex.Replace(rawText, @"\s+", " ").Trim();
        }
        #endregion
    }
}