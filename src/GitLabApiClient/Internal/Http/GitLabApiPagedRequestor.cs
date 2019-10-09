using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using GitLabApiClient.Internal.Utilities;

namespace GitLabApiClient.Internal.Http
{
    internal enum PageFetchStrategy
    {
        SinglePage,
        Total,
        Next
    }

    internal sealed class GitLabApiPagedRequestor
    {
        private const int MaxItemsPerPage = 100;

        private readonly GitLabApiRequestor _requestor;

        public GitLabApiPagedRequestor(GitLabApiRequestor requestor) => _requestor = requestor;

        public async IAsyncEnumerable<IList<T>> GetPageEnumeration<T>(string url, int bufferedPages = 3, int firstPage = 1)
        {
            if (bufferedPages < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferedPages), "buffered page count must be positive");
            }

            if (firstPage < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(firstPage), "first page index must be positive");
            }

            var (firstResult, headers) = await _requestor.GetWithHeaders<IList<T>>(GetPagedUrl(url, firstPage));
            var (strategy, totalPages) = GetFetchStrategy(headers);
            switch (strategy)
            {
                //return await GetNextPageList(url, 2, result);
                case PageFetchStrategy.SinglePage:
                    yield return firstResult;
                    yield break;
                case PageFetchStrategy.Next:
                {
                    var request = Task.FromResult((result: firstResult, headers));
                    while (request != null)
                    {
                        var currentResult = await request;
                        int nextPage = currentResult.headers.GetFirstHeaderValueOrDefault<int>("X-Total-Pages");
                        if (nextPage > 1)
                        {
                            string pagedUrl = GetPagedUrl(url, nextPage);

                            request = _requestor.GetWithHeaders<IList<T>>(pagedUrl);
                        }
                        else
                        {
                            request = null;
                        }

                        yield return currentResult.result;
                    }

                    yield break;
                }
                case PageFetchStrategy.Total:
                {
                    var requestQueue = new Queue<Task<IList<T>>>();
                    int nextFetchedPageIndex = 2;
                    requestQueue.Enqueue(Task.FromResult(firstResult));
                    while (requestQueue.Count != 0)
                    {
                        var currentResult = requestQueue.Dequeue();
                        while (requestQueue.Count < bufferedPages && nextFetchedPageIndex <= totalPages.Value)
                        {
                            string pagedUrl = GetPagedUrl(url, nextFetchedPageIndex);
                            requestQueue.Enqueue(_requestor.Get<IList<T>>(pagedUrl));
                            nextFetchedPageIndex++;
                        }

                        yield return await currentResult;
                    }

                    yield break;
                }
            }
        }

        public async Task<IList<T>> GetPagedList<T>(string url)
        {
            var result = new List<T>();

            //make first request and it will get available pages in the headers
            var (results, headers) = await _requestor.GetWithHeaders<IList<T>>(GetPagedUrl(url, 1));
            result.AddRange(results);
            var (strategy, totalPages) = GetFetchStrategy(headers);
            switch (strategy)
            {
                case PageFetchStrategy.Next:
                    return await GetNextPageList(url, 2, result);
                case PageFetchStrategy.SinglePage:
                    return result;
                case PageFetchStrategy.Total:
                    return await GetTotalPagedList(url, totalPages.Value, result);
                default:
                    throw new Exception("Unknown fetch strategy");
            }
        }

        private (PageFetchStrategy, int? totalPageNumbers) GetFetchStrategy(HttpResponseHeaders headers)
        {
            int totalPages = headers.GetFirstHeaderValueOrDefault<int>("X-Total-Pages");
            int nextPage = headers.GetFirstHeaderValueOrDefault<int>("X-Next-Page");

            switch (totalPages)
            {
                // X-Total-Pages is not always present due to performance concern so we have to take the slow path of nextPage
                case 0 when nextPage > 1:
                    return (PageFetchStrategy.Next, null);
                case 0:
                case 1:
                    return (PageFetchStrategy.SinglePage, null);
                default:
                    return (PageFetchStrategy.Total, totalPages);
            }
        }

        private async Task<IList<T>> GetNextPageList<T>(string url, int nextPage, List<T> result)
        {
            do
            {
                string pagedUrl = GetPagedUrl(url, nextPage);
                var (results, headers) = await _requestor.GetWithHeaders<IList<T>>(pagedUrl);
                result.AddRange(results);
                nextPage = headers.GetFirstHeaderValueOrDefault<int>("X-Next-Page");
            }
            while (nextPage > 1);

            return result;
        }

        private async Task<IList<T>> GetTotalPagedList<T>(string url, int totalPages, List<T> result)
        {
            //get paged urls
            var pagedUrls = GetPagedUrls(url, totalPages);
            if (pagedUrls.Count == 0)
                return result;

            int partitionSize = Environment.ProcessorCount;
            var remainingUrls = pagedUrls;
            do
            {
                var responses = remainingUrls.Take(partitionSize).Select(
                    async u => await _requestor.Get<IList<T>>(u));

                var results = await Task.WhenAll(responses);
                result.AddRange(results.SelectMany(r => r));
                remainingUrls = remainingUrls.Skip(partitionSize).ToList();
            }
            while (remainingUrls.Any());

            return result;
        }

        private static List<string> GetPagedUrls(string originalUrl, int totalPages)
        {
            var pagedUrls = new List<string>();

            for (int i = 2; i <= totalPages; i++)
                pagedUrls.Add(GetPagedUrl(originalUrl, i));

            return pagedUrls;
        }

        private static string GetPagedUrl(string url, int pageNumber)
        {
            string parameterSymbol = url.Contains("?") ? "&" : "?";
            return $"{url}{parameterSymbol}per_page={MaxItemsPerPage}&page={pageNumber}";
        }
    }

    internal static class HttpResponseHeadersExtensions
    {
        public static T GetFirstHeaderValueOrDefault<T>(
            this HttpResponseHeaders headers,
            string headerKey)
        {
            var toReturn = default(T);

            if (!headers.TryGetValues(headerKey, out var headerValues))
                return toReturn;

            string valueString = headerValues.FirstOrDefault();
            return valueString.IsNullOrEmpty() ? toReturn : (T)Convert.ChangeType(valueString, typeof(T));
        }
    }
}
