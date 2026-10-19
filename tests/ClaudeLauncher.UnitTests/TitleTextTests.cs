using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.UnitTests;

public class TitleTextTests
{
    [Theory]
    // A file mentioned to Claude Code: its name, not its whole path.
    [InlineData("@\"G:\\ABC中期\\论文2026\\撰写\\润色稿(3).docx\" 角色设定：你是审稿人", "润色稿(3).docx 角色设定：你是审稿人")]
    [InlineData("@\"C:\\Users\\me\\Downloads\\调查表(1).pdf\" /superpowers", "调查表(1).pdf /superpowers")]
    [InlineData("看看 @src/app/main.cs 这里", "看看 main.cs 这里")]
    [InlineData("@\"D:\\资料\\\" 整理一下", "资料 整理一下")]
    // A Markdown link: its words.
    [InlineData("[针对《某期刊》的写作大纲.md](//wsl.localhost/Ubuntu/home/x/大纲.md) 帮我改",
        "针对《某期刊》的写作大纲.md 帮我改")]
    [InlineData("[GFL代码.sh](374份/GFL代码.sh) 参考数据位置", "GFL代码.sh 参考数据位置")]
    // A target with a space in it, and one the app cut off before it closed.
    [InlineData("[大纲.md](//wsl.localhost/home/Meteorological Data/大纲.md) 改", "大纲.md 改")]
    [InlineData("[针对《某期刊》的大纲.md](//wsl.localhost/Ubuntu/home/Meteorological Data/针对《The Cro",
        "针对《某期刊》的大纲.md")]
    // Left alone: an e-mail address, an @ without a path, brackets that are no link.
    [InlineData("联系 a@b.com 获取", "联系 a@b.com 获取")]
    [InlineData("@张三 你好", "@张三 你好")]
    [InlineData("[草稿] 第二版", "[草稿] 第二版")]
    // One line.
    [InlineData("第一行\n\n第二行\t尾", "第一行 第二行 尾")]
    public void ATitleShowsWhatWasMeantNotTheMarkup(string typed, string title)
    {
        Assert.Equal(title, TitleText.Tidy(typed));
    }

    [Fact]
    public void ATitleIsCutToTheListsLength()
    {
        Assert.Equal(TitleText.MaxLength, TitleText.Tidy(new string('长', 300)).Length);
    }
}
