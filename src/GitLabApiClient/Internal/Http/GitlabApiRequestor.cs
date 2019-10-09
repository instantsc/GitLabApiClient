using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GitLabApiClient.Internal.Http.Serialization;
using GitLabApiClient.Models.Uploads.Requests;
using GitLabApiClient.Models.Uploads.Responses;

namespace GitLabApiClient.Internal.Http
{
    internal sealed class GitLabApiRequestor
    {
        private readonly HttpClient _client;
        private readonly RequestsJsonSerializer _jsonSerializer;
        //how much we can request at once before waiting for RPS cooldown
        private const int BatchThreshold = 5;
        private readonly SemaphoreSlim _requestSemaphore = new SemaphoreSlim(BatchThreshold, BatchThreshold);
        public int MaximalRequestsPerSecond { get; set; } = 10;

        private async Task<T> PerformRateLimitedAction<T>(Func<Task<T>> action)
        {
            await _requestSemaphore.WaitAsync();

            var delay = TimeSpan.FromSeconds(BatchThreshold / (double)MaximalRequestsPerSecond);
            _ = ReleaseSemaphoreAfterDelay(delay);
            return await action();
        }

        private async Task ReleaseSemaphoreAfterDelay(TimeSpan delay)
        {
            await Task.Delay(delay);
            _requestSemaphore.Release();
        }

        private async Task<HttpResponseMessage> PostMethod(string url, HttpContent content) => await PerformRateLimitedAction(async () => await _client.PostAsync(url, content));
        private async Task<HttpResponseMessage> GetMethod(string url) => await PerformRateLimitedAction(async () => await _client.GetAsync(url));
        private async Task<HttpResponseMessage> PutMethod(string url, HttpContent content) => await PerformRateLimitedAction(async () => await _client.PutAsync(url, content));
        private async Task<HttpResponseMessage> DeleteMethod(string url) => await PerformRateLimitedAction(async () => await _client.DeleteAsync(url));


        public GitLabApiRequestor(HttpClient client, RequestsJsonSerializer jsonSerializer)
        {
            _client = client;
            _jsonSerializer = jsonSerializer;
        }

        public async Task<T> Get<T>(string url)
        {
            var responseMessage = await GetMethod(url);
            await EnsureSuccessStatusCode(responseMessage);
            return await ReadResponse<T>(responseMessage);
        }

        public async Task<T> Post<T>(string url, object data = null)
        {
            StringContent content = SerializeToString(data);
            var responseMessage = await PostMethod(url, content);
            await EnsureSuccessStatusCode(responseMessage);
            return await ReadResponse<T>(responseMessage);
        }

        public async Task Post(string url, object data = null)
        {
            StringContent content = SerializeToString(data);
            var responseMessage = await PostMethod(url, content);
            await EnsureSuccessStatusCode(responseMessage);
        }

        public async Task<Upload> PostFile(string url, CreateUploadRequest uploadRequest)
        {
            using (var uploadContent =
                new MultipartFormDataContent($"Upload----{DateTime.Now.Ticks}"))
            {
                uploadContent.Add(new StreamContent(uploadRequest.Stream), "file", uploadRequest.FileName);

                var responseMessage = await PostMethod(url, uploadContent);
                await EnsureSuccessStatusCode(responseMessage);

                return await ReadResponse<Upload>(responseMessage);
            }
        }


        public async Task<T> Put<T>(string url, object data)
        {
            StringContent content = SerializeToString(data);
            var responseMessage = await PutMethod(url, content);
            await EnsureSuccessStatusCode(responseMessage);
            return await ReadResponse<T>(responseMessage);
        }

        public async Task Put(string url, object data)
        {
            StringContent content = SerializeToString(data);
            var responseMessage = await PutMethod(url, content);
            await EnsureSuccessStatusCode(responseMessage);
        }

        public async Task Delete(string url)
        {
            var responseMessage = await DeleteMethod(url);
            await EnsureSuccessStatusCode(responseMessage);
        }

        public async Task<ValueTuple<T, HttpResponseHeaders>> GetWithHeaders<T>(string url)
        {
            var responseMessage = await GetMethod(url);
            await EnsureSuccessStatusCode(responseMessage);
            return (await ReadResponse<T>(responseMessage), responseMessage.Headers);
        }

        private static async Task EnsureSuccessStatusCode(HttpResponseMessage responseMessage)
        {
            if (responseMessage.IsSuccessStatusCode)
                return;

            string errorResponse = await responseMessage.Content.ReadAsStringAsync();
            throw new GitLabException(responseMessage.StatusCode, errorResponse ?? "");
        }

        private async Task<T> ReadResponse<T>(HttpResponseMessage responseMessage)
        {
            string response = await responseMessage.Content.ReadAsStringAsync();
            var result = _jsonSerializer.Deserialize<T>(response);
            return result;
        }

        private StringContent SerializeToString(object data)
        {
            string serializedObject = _jsonSerializer.Serialize(data);

            var content = data != null ?
                new StringContent(serializedObject) :
                new StringContent(string.Empty);

            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }
    }
}
