using System;
using System.Collections.Generic;
using System.Linq;

namespace CrawlerCore.Common
{
    /// <summary>
    /// 集合处理工具类
    /// </summary>
    public static class CollectionHelper
    {
        /// <summary>
        /// 判断泛型集合是否为空或null
        /// </summary>
        /// <typeparam name="T">集合元素类型</typeparam>
        /// <param name="collection">待判断的集合</param>
        /// <returns>为空/null返回true，否则返回false</returns>
        /// <remarks>优先判断ICollection的Count，避免大集合全量遍历，提升性能</remarks>
        public static bool IsNullOrEmpty<T>(IEnumerable<T> collection)
        {
            if (collection == null) return true;
            if (collection is ICollection<T> coll) return coll.Count == 0;
            return !collection.Any();
        }

        /// <summary>
        /// 泛型集合去重（默认使用元素默认比较器）
        /// </summary>
        /// <typeparam name="T">集合元素类型</typeparam>
        /// <param name="collection">待去重的集合（若为null，返回空集合）</param>
        /// <returns>去重后的新集合（IEnumerable，需ToList()执行）</returns>
        public static IEnumerable<T> RemoveDuplicates<T>(IEnumerable<T> collection)
        {
            if (IsNullOrEmpty(collection)) return Enumerable.Empty<T>();
            return collection.Distinct();
        }

        /// <summary>
        /// 泛型集合分页（页码从1开始）
        /// </summary>
        /// <typeparam name="T">集合元素类型</typeparam>
        /// <param name="collection">待分页的集合</param>
        /// <param name="pageIndex">页码（≥1，否则视为1）</param>
        /// <param name="pageSize">页大小（≥1，否则视为10）</param>
        /// <returns>分页后的子集（IEnumerable，需ToList()执行；超出范围返回空集合）</returns>
        public static IEnumerable<T> Pagination<T>(IEnumerable<T> collection, int pageIndex, int pageSize)
        {
            if (IsNullOrEmpty(collection)) return Enumerable.Empty<T>();

            pageIndex = pageIndex < 1 ? 1 : pageIndex;
            pageSize = pageSize < 1 ? 10 : pageSize;

            return collection.Skip((pageIndex - 1) * pageSize).Take(pageSize);
        }
    }
}