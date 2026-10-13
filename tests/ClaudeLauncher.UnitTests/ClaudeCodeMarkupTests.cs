using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.UnitTests;

public sealed class ClaudeCodeMarkupTests
{
    [Theory]
    // What typing "/loop …" records (message first) and what a built-in records (name first).
    [InlineData("<command-message>loop</command-message>\n<command-name>/loop</command-name>\n<command-args>每天检查一次</command-args>",
        "/loop", "每天检查一次")]
    [InlineData("<command-name>/model</command-name>\n            <command-message>model</command-message>\n            <command-args>claude-fable-5-1</command-args>",
        "/model", "claude-fable-5-1")]
    [InlineData("<command-message>init</command-message>\n<command-name>/init</command-name>", "/init", "")]
    // A skill the model loaded itself: no slash, and an extra element.
    [InlineData("<command-message>docx</command-message>\n<command-name>docx</command-name>\n<skill-format>true</skill-format>",
        "/docx", "")]
    [InlineData("<command-name>/superpowers</command-name><command-args>  当前有哪些功能 \n</command-args>",
        "/superpowers", "当前有哪些功能")]
    public void ReadsARecordedCommand(string text, string name, string arguments)
    {
        Assert.True(ClaudeCodeMarkup.TryReadCommand(text, out var command));
        Assert.Equal(name, command.Name);
        Assert.Equal(arguments, command.Arguments);
    }

    [Theory]
    [InlineData("<command-name>/compact</command-name> 以后一律这样")]    // prose after the markup
    [InlineData("请看 <command-name>/loop</command-name>")]              // prose before it
    [InlineData("<command-message>loop</command-message>")]               // no command name
    [InlineData("<command-name>/a</command-name><command-name>/b</command-name>")]
    [InlineData("<command-name>/loop")]                                   // never closed
    [InlineData("<Command-Name>/loop</Command-Name>")]                    // not the app's markup
    [InlineData("<system-reminder>hook output</system-reminder>")]
    [InlineData("/loop 每天检查一次")]
    [InlineData("")]
    public void AnythingElseIsNotACommand(string text)
    {
        Assert.False(ClaudeCodeMarkup.TryReadCommand(text, out _));
    }

    [Fact]
    public void TheAppsBlocksComeOffTheFrontOfWhatWasTyped()
    {
        // What Claude's desktop app records for the first message of a session without a folder.
        var (blocks, rest) = ClaudeCodeMarkup.PeelInjected(
            "<system-reminder>\nThe user started this session without choosing a project folder.\n</system-reminder>\n\n帮我整理数据");
        Assert.Equal("帮我整理数据", rest);
        Assert.StartsWith("<system-reminder>", Assert.Single(blocks), StringComparison.Ordinal);

        // Two in a row — the IDE extension's context as well — then the words.
        Assert.Equal("问题", ClaudeCodeMarkup.UsersWords(
            "<ide_opened_file>The user opened a.cs</ide_opened_file> <system-reminder>y</system-reminder>问题"));

        // Nothing but blocks, an unclosed block, prose first, markup that is not injected
        // context, command markup with prose after it, or a command: left as it was.
        Assert.Equal("<system-reminder>只有提醒</system-reminder>",
            ClaudeCodeMarkup.UsersWords("<system-reminder>只有提醒</system-reminder>"));
        Assert.Equal("<system-reminder>没有结束 问题", ClaudeCodeMarkup.UsersWords("<system-reminder>没有结束 问题"));
        Assert.Equal("问题 <system-reminder>x</system-reminder>",
            ClaudeCodeMarkup.UsersWords("问题 <system-reminder>x</system-reminder>"));
        Assert.Equal("<div>x</div>问题", ClaudeCodeMarkup.UsersWords("<div>x</div>问题"));
        Assert.Equal("<command-name>/compact</command-name> 以后一律这样",
            ClaudeCodeMarkup.UsersWords("<command-name>/compact</command-name> 以后一律这样"));
        const string command = "<command-name>/loop</command-name><command-args>每天检查</command-args>";
        Assert.Equal(command, ClaudeCodeMarkup.UsersWords(command));
    }

    [Fact]
    public void TypedFormIsTheCommandAndItsArguments()
    {
        Assert.Equal("/loop 每天检查一次", new SlashCommand("/loop", "每天检查一次").Typed);
        Assert.Equal("/init", new SlashCommand("/init", "").Typed);
    }
}
