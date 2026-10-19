using ClaudeLauncher.Infrastructure.Catalog;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// The schema travels inside the assembly, so a single .exe handed to someone else can create
/// its catalog on first run with no file beside it.
/// </summary>
public sealed class CatalogSchemaTextTests
{
    [Fact]
    public void TheBuiltInSchemaIsTheSchemaFile()
    {
        var file = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql"));

        Assert.Equal(file, CatalogSchemaText.Load());
        Assert.Contains("CREATE TABLE", CatalogSchemaText.Load(), StringComparison.OrdinalIgnoreCase);
    }
}
