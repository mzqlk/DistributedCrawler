using CrawlerCore.Infrastructure;
using CrawlerWorker.Services.Engine.Robots;

namespace CrawlerCore.Tests.Worker;

/// <summary>robots.txt 解析与匹配（W-07，RFC 9309）</summary>
public class RobotsTxtTests
{
    private const string Token = "DistributedCrawler";

    private static bool Allowed(RobotsTxt robots, string pathAndQuery) => robots.IsAllowed(new Uri("https://site.test" + pathAndQuery));

    [Fact(DisplayName = "最长匹配优先；长度相同时 Allow 优先")]
    public void LongestMatchWins_AllowWinsTies()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /a\nAllow: /a/b\nDisallow: /a/b/c\nAllow: /x\nDisallow: /x\n", Token);

        Assert.False(Allowed(robots, "/a/z"));
        Assert.True(Allowed(robots, "/a/b/z"));
        Assert.False(Allowed(robots, "/a/b/c/d"));
        Assert.True(Allowed(robots, "/x/1"));
        Assert.True(Allowed(robots, "/other"));
    }

    [Fact(DisplayName = "通配符 * 与结尾锚定 $；路径大小写敏感；匹配包含查询串")]
    public void WildcardsAndAnchors()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /search\nDisallow: /*?print=\n", Token);

        Assert.False(Allowed(robots, "/papers/a.pdf"));
        Assert.True(Allowed(robots, "/papers/a.pdf.html"));
        Assert.False(Allowed(robots, "/search/?query=x"));
        Assert.True(Allowed(robots, "/Search"));
        Assert.False(Allowed(robots, "/page?print=1"));
    }

    [Fact(DisplayName = "产品名匹配的规则组优先于 *，同名的多个组合并；Crawl-delay 取所选组的值")]
    public void ProductGroupPreferredOverWildcard()
    {
        const string content = """
            # 注释行
            User-agent: *
            Disallow: /
            Crawl-delay: 15

            User-agent: distributedcrawler
            Disallow: /private
            Crawl-delay: 3

            User-agent: OtherBot
            User-agent: DistributedCrawler
            Disallow: /tmp   # 行尾注释
            """;

        var robots = RobotsTxt.Parse(content, Token);

        Assert.True(Allowed(robots, "/public"));
        Assert.False(Allowed(robots, "/private/1"));
        Assert.False(Allowed(robots, "/tmp/1"));
        Assert.Equal(TimeSpan.FromSeconds(3), robots.CrawlDelay);
    }

    [Fact(DisplayName = "没有匹配的组时使用 * 组（arXiv 的写法：Crawl-delay 15，禁止 /search，允许 /abs 与 /list）")]
    public void WildcardGroup_ArxivStyle()
    {
        const string content = """
            User-agent: *
            Crawl-delay: 15
            Allow: /abs
            Allow: /list
            Disallow: /search
            Disallow: /api
            """;

        var robots = RobotsTxt.Parse(content, Token);

        Assert.True(Allowed(robots, "/abs/2401.01234"));
        Assert.True(Allowed(robots, "/list/cs.LG/recent"));
        Assert.False(Allowed(robots, "/search/?query=machine+learning"));
        Assert.Equal(TimeSpan.FromSeconds(15), robots.CrawlDelay);
    }

    [Fact(DisplayName = "空的 Disallow 不构成规则；没有任何组、空内容时允许一切；/robots.txt 总是允许")]
    public void EmptyRulesAndRobotsTxtItself()
    {
        Assert.True(Allowed(RobotsTxt.Parse("User-agent: *\nDisallow:\n", Token), "/anything"));
        Assert.True(Allowed(RobotsTxt.Parse("Disallow: /\n", Token), "/anything"));
        Assert.True(Allowed(RobotsTxt.Parse(string.Empty, Token), "/anything"));
        Assert.True(Allowed(RobotsTxt.Parse("User-agent: *\nDisallow: /\n", Token), "/robots.txt"));
        Assert.False(Allowed(RobotsTxt.Parse("User-agent: *\nDisallow: /\n", Token), "/"));
    }

    [Fact(DisplayName = "产品名取自 User-Agent 的第一个 “/” 之前")]
    public void ProductTokenFromUserAgent()
    {
        Assert.Equal("DistributedCrawler", RobotsPolicy.GetProductToken(new CrawlerSettings().DefaultUserAgent));
        Assert.Equal("Bot", RobotsPolicy.GetProductToken("Bot"));
        Assert.Equal("*", RobotsPolicy.GetProductToken(null));
    }
}
