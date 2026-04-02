using Xunit;
using CrawlerCore.Common;
using System.Collections.Generic;
using System.Linq;

namespace CrawlerCore.Tests.Common
{
    public class CollectionHelperTests
    {
        [Fact]
        public void IsNullOrEmpty_EmptyCollection_ReturnsTrue()
        {
            Assert.True(CollectionHelper.IsNullOrEmpty<int>(null));
            Assert.True(CollectionHelper.IsNullOrEmpty(Enumerable.Empty<int>()));
        }

        [Fact]
        public void RemoveDuplicates_DuplicateElements_ReturnsUnique()
        {
            var input = new List<int> { 1, 2, 2, 3, 3, 3 };
            var result = CollectionHelper.RemoveDuplicates(input).ToList();
            Assert.Equal(3, result.Count);
            Assert.Equal(new List<int> { 1, 2, 3 }, result);
        }

        [Fact]
        public void Pagination_Page1Size2_ReturnsFirstTwo()
        {
            var input = Enumerable.Range(1, 5); // [1,2,3,4,5]
            var result = CollectionHelper.Pagination(input, 1, 2).ToList();
            Assert.Equal(new List<int> { 1, 2 }, result);
        }

        [Fact]
        public void Pagination_OutOfRange_ReturnsEmpty()
        {
            var input = Enumerable.Range(1, 5);
            var result = CollectionHelper.Pagination(input, 4, 2).ToList(); // 页码4超出
            Assert.Empty(result);
        }

        [Fact]
        public void Pagination_PageIndex0OrPageSize0_UsesDefaultValues()
        {
            var input = Enumerable.Range(1, 5);
            // pageIndex=0 → 视为1，pageSize=0 → 视为10
            var result = CollectionHelper.Pagination(input, 0, 0).ToList();
            Assert.Equal(input.ToList(), result); // 页大小10，包含所有5条数据
        }

        [Fact]
        public void RemoveDuplicates_NullInput_ReturnsEmpty()
        {
            var result = CollectionHelper.RemoveDuplicates<int>(null).ToList();
            Assert.Empty(result);
        }

        [Fact]
        public void IsNullOrEmpty_ICollectionEmpty_ReturnsTrue()
        {
            var emptyList = new List<int>(); // ICollection类型
            Assert.True(CollectionHelper.IsNullOrEmpty(emptyList));
        }
    }
}