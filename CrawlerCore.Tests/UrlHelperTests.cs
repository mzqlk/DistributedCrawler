using Xunit;
using CrawlerCore.Common;

namespace CrawlerCore.Tests.Common
{
    public class UrlHelperTests
    {
        [Fact]
        public void IsValidUrl_ValidHttpsUrl_ReturnsTrue()
        {
            bool result = UrlHelper.IsValidUrl("https://example.com");
            Assert.True(result);
        }

        [Fact]
        public void IsValidUrl_InvalidUrl_NoProtocol_ReturnsFalse()
        {
            bool result = UrlHelper.IsValidUrl("example.com");
            Assert.False(result);
        }

        [Fact]
        public void IsValidUrl_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(UrlHelper.IsValidUrl(null));
            Assert.False(UrlHelper.IsValidUrl(""));
            Assert.False(UrlHelper.IsValidUrl("   "));
        }

        [Fact]
        public void CombineUrl_BaseWithSlashAndRelativeWithSlash_RemovesDuplicateSlash()
        {
            string result = UrlHelper.CombineUrl("https://example.com/", "/path");
            Assert.Equal("https://example.com/path", result);
        }

        [Fact]
        public void CombineUrl_BaseWithoutSlashAndRelativeWithoutSlash_AddsSlash()
        {
            string result = UrlHelper.CombineUrl("https://example.com", "path");
            Assert.Equal("https://example.com/path", result);
        }

        [Fact]
        public void CombineUrl_BaseAndRelativeBothNull_ReturnsEmpty()
        {
            string result = UrlHelper.CombineUrl(null, null);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void CombineUrl_BaseWithQueryParam_ReturnsPathAfterQuery()
        {
            // 说明：此工具类不处理查询参数，仅拼接路径，结果符合预期
            string result = UrlHelper.CombineUrl("https://example.com?a=1", "b=2");
            Assert.Equal("https://example.com?a=1/b=2", result);
        }
    }
}