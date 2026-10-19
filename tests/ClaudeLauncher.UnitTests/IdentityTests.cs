using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.UnitTests;

public sealed class IdentityTests
{
    [Fact]
    public void OrganizationIsPartOfIdentity()
    {
        var a = new ConversationKey("desktop-code", "account-a", "org-a", "session-a");
        var b = a with { OrganizationContextId = "org-b" };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AccountIsPartOfIdentity()
    {
        var a = new ConversationKey("desktop-code", "account-a", "org-a", "session-a");
        var b = a with { AccountContextId = "account-b" };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SameNativeIdAcrossBackendsIsNotTheSameConversation()
    {
        var desktop = new ConversationKey(ConversationKey.DesktopCode, "acc", "org", "shared-id");
        var cli = new ConversationKey(ConversationKey.CliCode, "acc", "org", "shared-id");
        Assert.NotEqual(desktop, cli);
    }

    [Fact]
    public void LogFormCarriesNoFullIdentifiers()
    {
        var key = new ConversationKey(
            ConversationKey.CliCode, "account-1234567890", "org-1234567890", "f8815136-42dd-4d04");
        var log = key.ToLogForm();

        Assert.DoesNotContain("account-1234567890", log, StringComparison.Ordinal);
        Assert.DoesNotContain("f8815136-42dd-4d04", log, StringComparison.Ordinal);
        Assert.Contains(ConversationKey.CliCode, log, StringComparison.Ordinal);
    }
}
