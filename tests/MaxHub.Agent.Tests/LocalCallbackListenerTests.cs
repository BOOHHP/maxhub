using MaxHub.Agent.Core.Remote;

namespace MaxHub.Agent.Tests;

public class LocalCallbackListenerTests
{
    [Fact]
    public async Task Receives_code_and_state_from_redirect()
    {
        using var listener = LocalCallbackListener.Start(port: 0); // 测试用临时端口
        var waitTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10));

        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{listener.Port}/callback?code=abc%2B123&state=session-xyz");
        Assert.True(response.IsSuccessStatusCode);

        var callback = await waitTask;
        Assert.NotNull(callback);
        Assert.Equal("abc+123", callback.Code); // URL 解码
        Assert.Equal("session-xyz", callback.State);
    }

    [Fact]
    public async Task Missing_code_returns_null_and_400()
    {
        using var listener = LocalCallbackListener.Start(port: 0);
        var waitTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(10));

        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{listener.Port}/callback?state=only-state");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await waitTask);
    }

    [Fact]
    public async Task Times_out_when_no_callback_arrives()
    {
        using var listener = LocalCallbackListener.Start(port: 0);
        Assert.Null(await listener.WaitForCallbackAsync(TimeSpan.FromMilliseconds(200)));
    }
}
