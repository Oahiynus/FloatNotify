using System.Net;
using System.Net.Http;

namespace FloatNotify;

internal sealed record ReleaseUpdate(string Message, Uri? ReleasePage = null);

internal static class ReleaseUpdates
{
    const string Releases = "https://github.com/Oahiynus/FloatNotify/releases/";
    static readonly Uri Latest = new(Releases + "latest");
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    internal static async Task<ReleaseUpdate> CheckAsync(string installed, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        request.Headers.UserAgent.ParseAdd("FloatNotify");
        try
        {
            // The official latest-release endpoint redirects to the tag. Only its headers are needed.
            using var response = await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new("未找到浮笺的正式发布版本，请稍后重试。");
            if ((int)response.StatusCode is < 300 or >= 400)
            {
                response.EnsureSuccessStatusCode();
                return new("GitHub 未返回正式版标签，请稍后重试。");
            }
            var location = response.Headers.Location;
            if (location == null || !Uri.TryCreate(Latest, location, out var page) ||
                !page.AbsoluteUri.StartsWith(Releases + "tag/v", StringComparison.Ordinal))
                return new("无法识别浮笺的正式版标签，请稍后重试。");

            var number = page.AbsoluteUri[(Releases + "tag/v").Length..];
            if (!Version.TryParse(number, out var latest) || latest.Build < 0 || latest.Revision >= 0 || latest.ToString() != number)
                return new("无法识别浮笺的正式版标签，请稍后重试。");
            if (!Version.TryParse(installed, out var current))
                return new("无法识别当前浮笺版本。");

            int order = current.CompareTo(current.Revision < 0 ? latest : new Version(latest.Major, latest.Minor, latest.Build, 0));
            if (order < 0) return new($"发现新版本 v{latest}。", page);
            if (order > 0) return new($"当前版本高于最新正式版 v{latest}。");
            return new($"当前已是最新正式版 v{latest}。");
        }
        catch (OperationCanceledException) { return new("连接 GitHub 超时，请重试。"); }
        catch (HttpRequestException ex)
        {
            return new(ex.StatusCode is {} status
                ? $"GitHub 暂时无法提供版本信息（HTTP {(int)status}），请稍后重试。"
                : "无法连接 GitHub，请检查网络后重试。");
        }
    }
}
