using Jellyfin.Plugin.WatchOrder.WebClient;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public class ScriptInjectorTests
{
    [Fact]
    public void Inject_AddsScriptBeforeBodyEnd()
    {
        var html = ScriptInjector.Inject("<html><body><div></div></body></html>", "0.3.0.0");

        Assert.EndsWith("<script defer src=\"../WatchOrder/ClientScript?v=0.3.0.0\" data-watch-order-client></script></body></html>", html);
    }

    [Fact]
    public void Inject_IsIdempotent()
    {
        var once = ScriptInjector.Inject("<html><body></body></html>", "1");

        Assert.Equal(once, ScriptInjector.Inject(once, "1"));
    }

    [Theory]
    [InlineData("(self.webpackChunk=self.webpackChunk||[]).push([[17244],{1:function(){}}]);")] // session-login-index-html chunk
    [InlineData("<p>no body tag</p>")]
    [InlineData("<div>a fragment</div></body>")]
    [InlineData("var t='<html><body></body></html>';")] // JavaScript containing HTML in a string
    public void Inject_LeavesAnythingThatIsNotTheHtmlPageAlone(string content)
    {
        Assert.Equal(content, ScriptInjector.Inject(content, "1"));
    }

    [Fact]
    public void Inject_HandlesDoctypeAndLeadingWhitespace()
    {
        var html = ScriptInjector.Inject("\n  <!DOCTYPE html><html><head></head><body></body></html>", "1");

        Assert.Contains("data-watch-order-client></script></body>", html);
    }

    [Fact]
    public void Inject_LeavesEmptyContentAlone()
    {
        Assert.Equal(string.Empty, ScriptInjector.Inject(string.Empty, "1"));
    }
}
