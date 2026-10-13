namespace ClaudeLauncher.Infrastructure.Catalog;

/// <summary>
/// The catalog's schema, compiled into the assembly. It used to be read from a file beside
/// the executable, which a single-file build — one .exe to hand to someone else — does not
/// carry, so a fresh machine could not create its catalog.
/// </summary>
public static class CatalogSchemaText
{
    public const string ResourceName = "CatalogSchema.sql";

    public static string Load()
    {
        using var stream = typeof(CatalogSchemaText).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("程序缺少内置的目录结构定义 " + ResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
