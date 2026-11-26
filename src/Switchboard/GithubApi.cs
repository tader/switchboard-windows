using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
namespace Switchboard;

public static class GithubApi {
    static readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(60) };
    public static HttpRequestMessage Request(Uri uri, string? token) {
        if (uri.Scheme!="https" || uri.Host!="api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length!=0) throw new Exception("Untrusted GitHub API origin.");
        var request = new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.UserAgent.ParseAdd("switchboard-windows");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if(token!=null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",GithubTokenStore.Validate(token));
        return request;
    }
    public static string Error(HttpResponseMessage response, bool secondaryRateLimited=false) {
        if(response.StatusCode==HttpStatusCode.Unauthorized)return "GitHub rejected the token. Remove it under GitHub token in Settings, then save a new token.";
        var limited=secondaryRateLimited || response.StatusCode==(HttpStatusCode)429 || response.StatusCode==HttpStatusCode.Forbidden && (response.Headers.TryGetValues("x-ratelimit-remaining",out var remaining) && remaining.Contains("0") || response.Headers.RetryAfter!=null);
        if(limited) {
            var timing=response.Headers.RetryAfter?.Delta is TimeSpan delay ? $" Retry after {Math.Ceiling(delay.TotalSeconds)} seconds." : response.Headers.RetryAfter?.Date is DateTimeOffset date ? $" Retry after {date.ToLocalTime():g}." : "";
            if(timing.Length==0 && response.Headers.TryGetValues("x-ratelimit-reset",out var reset) && long.TryParse(reset.FirstOrDefault(),out var seconds) && seconds>=0 && seconds<=253402300799)timing=$" Retry after {DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime():g}.";
            return "GitHub rate limit reached. Open Settings and scroll below Updates to GitHub token."+timing;
        }
        if(response.StatusCode==HttpStatusCode.Forbidden)return "GitHub denied access. Check the token permissions or remove it under GitHub token in Settings.";
        if(response.StatusCode==HttpStatusCode.NotFound)return "The requested GitHub release or revision was not found.";
        return "Could not load GitHub release metadata. Check your connection and retry.";
    }
    public static async Task<string> Get(string url,CancellationToken cancellation=default) {
        using var request=Request(new Uri(url),new GithubTokenStore().Read());
        using var response=await client.SendAsync(request,cancellation);
        if(!response.IsSuccessStatusCode) {
            var secondary=false;
            if(response.StatusCode==HttpStatusCode.Forbidden) {
                try {
                    using var body=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                    secondary=body.RootElement.TryGetProperty("message",out var message) && message.ValueKind==System.Text.Json.JsonValueKind.String && message.GetString()!.Contains("rate limit",StringComparison.OrdinalIgnoreCase);
                } catch(System.Text.Json.JsonException) { }
            }
            throw new Exception(Error(response,secondary));
        }
        return await response.Content.ReadAsStringAsync(cancellation);
    }
    public static Dictionary<string,string> ServerEnvironment(Settings settings,string? token) {
        var env=new Dictionary<string,string> { ["SWITCHBOARD_HOST"]=settings.Host,["SWITCHBOARD_PORT"]=settings.Port.ToString(),["SWITCHBOARD_PUBLIC_URL"]=settings.ControlUrl,["SWITCHBOARD_LOCAL_ACCOUNT_RECOVERY"]="1" };
        if(token!=null)env["SWITCHBOARD_PLUGIN_INSTALL_GITHUB_TOKEN"]=token;
        return env;
    }
}
