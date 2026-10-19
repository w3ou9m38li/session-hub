using System.Reflection;
using System.Runtime.Loader;

namespace ClaudeLauncher.Infrastructure.Semantic;

/// <summary>
/// What is in place for semantic search, checked from the file system only — nothing is loaded.
/// </summary>
/// <param name="ComponentFolder">Where the optional component is looked for: <c>semantic</c> beside the exe.</param>
/// <param name="MissingFiles">Required model files not in <paramref name="ModelFolder"/>.</param>
/// <param name="ConfigError">What is wrong with embedding.json, when something is.</param>
public sealed record SemanticReadiness(
    string ComponentFolder, bool ComponentPresent, string ModelFolder, bool ModelFolderPresent,
    IReadOnlyList<string> MissingFiles, string? ConfigError)
{
    public bool Ready => ComponentPresent && ModelFolderPresent && MissingFiles.Count == 0 && ConfigError is null;

    /// <summary>What is missing, in words for the user; empty when ready.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();
            if (!ComponentPresent)
                problems.Add($"没有找到语义搜索组件：{Path.Combine(ComponentFolder, EmbedderLoader.ComponentAssembly)} 不存在。"
                             + $"把「语义搜索组件」压缩包解压到程序所在的文件夹 {Path.GetDirectoryName(ComponentFolder)}，让 semantic 文件夹在程序旁边。");
            if (!ModelFolderPresent)
                problems.Add($"没有找到向量模型文件夹：{ModelFolder}");
            else if (MissingFiles.Count > 0)
                problems.Add($"模型文件夹 {ModelFolder} 里缺少：{string.Join("、", MissingFiles)}");
            if (ConfigError is not null) problems.Add(ConfigError);
            return problems;
        }
    }
}

/// <summary>
/// Loads the embedding model through the optional component — <c>semantic\ClaudeLauncher.Semantic.dll</c>
/// beside the exe, published on its own with ONNX Runtime — into a load context of its own. Only the
/// worker process does this; the window checks the files (<see cref="Check"/>) and never loads anything.
/// </summary>
public static class EmbedderLoader
{
    public const string ComponentFolderName = "semantic";
    public const string ComponentAssembly = "ClaudeLauncher.Semantic.dll";
    private const string FactoryType = "ClaudeLauncher.Semantic.OnnxEmbedderFactory";
    private const string FactoryMethod = "Create";

    /// <summary>Where the component is looked for: a <c>semantic</c> folder beside the exe (the single-file exe too).</summary>
    public static string DefaultComponentFolder => Path.Combine(AppContext.BaseDirectory, ComponentFolderName);

    /// <summary>Whether the component and the model are in place, from the file system only.</summary>
    public static SemanticReadiness Check(string modelFolder, string? componentFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);
        componentFolder ??= DefaultComponentFolder;
        var component = File.Exists(Path.Combine(componentFolder, ComponentAssembly));
        var folder = Directory.Exists(modelFolder);
        var missing = folder
            ? new[] { EmbeddingConfig.ModelFile, EmbeddingConfig.VocabularyFile }.Where(f => !File.Exists(Path.Combine(modelFolder, f))).ToList()
            : [EmbeddingConfig.ModelFile, EmbeddingConfig.VocabularyFile];
        string? configError = null;
        if (folder)
        {
            try
            {
                EmbeddingConfig.Load(modelFolder);
            }
            catch (SemanticUnavailableException ex)
            {
                configError = ex.Message;
            }
        }
        return new SemanticReadiness(componentFolder, component, modelFolder, folder, missing, configError);
    }

    /// <summary>
    /// The model in <paramref name="modelFolder"/>, ready to embed, through the component in
    /// <paramref name="componentFolder"/> (beside the exe by default).
    /// </summary>
    /// <exception cref="SemanticUnavailableException">Something is missing or the model cannot be loaded; the message says what, for the user.</exception>
    public static IEmbedder Load(string modelFolder, string? componentFolder = null)
    {
        var readiness = Check(modelFolder, componentFolder);
        if (!readiness.Ready) throw new SemanticUnavailableException(string.Join("\n", readiness.Problems));

        var context = new ComponentLoadContext(Path.Combine(readiness.ComponentFolder, ComponentAssembly));
        object? created;
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(readiness.ComponentFolder, ComponentAssembly));
            var method = assembly.GetType(FactoryType, throwOnError: true)!
                .GetMethod(FactoryMethod, BindingFlags.Public | BindingFlags.Static, [typeof(string)])
                ?? throw new MissingMethodException(FactoryType, FactoryMethod);
            created = method.Invoke(null, [modelFolder]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is SemanticUnavailableException unavailable)
        {
            throw new SemanticUnavailableException(unavailable.Message, unavailable);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            throw new SemanticUnavailableException("向量模型加载不了：" + inner.Message, inner);
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException
                                       or TypeLoadException or MissingMethodException or DllNotFoundException)
        {
            throw new SemanticUnavailableException("语义搜索组件加载不了（可能与这个版本的会话管家不配套，请换用同一版本的组件）：" + ex.Message, ex);
        }
        return created as IEmbedder
               ?? throw new SemanticUnavailableException("语义搜索组件与这个版本的会话管家不配套，请换用同一版本的组件");
    }

    /// <summary>
    /// The component's own load context: its assemblies and native libraries come from its folder,
    /// as its .deps.json lists them, while those the host already has — this assembly and Core,
    /// whose interface types must be the host's own, and the framework — come from the host.
    /// </summary>
    private sealed class ComponentLoadContext : AssemblyLoadContext
    {
        private readonly string _folder;
        private readonly AssemblyDependencyResolver? _resolver;

        public ComponentLoadContext(string componentPath) : base("semantic component", isCollectible: false)
        {
            _folder = Path.GetDirectoryName(componentPath)!;
            try
            {
                _resolver = new AssemblyDependencyResolver(componentPath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                // A host that cannot resolve from a .deps.json: the published component is flat, so its folder will do.
                _resolver = null;
            }
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (IsHostAssembly(name.Name)) return null;
            // The framework's own, whenever the host has it; only what the host lacks — ONNX Runtime and
            // the packages it brings (System.Numerics.Tensors is one, not in the framework) — from the folder.
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
                // Not the host's: the component's.
            }
            var path = _resolver?.ResolveAssemblyToPath(name) ?? Flat(name.Name + ".dll");
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName)
                       ?? Flat(unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll");
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }

        private string? Flat(string file)
        {
            var path = Path.Combine(_folder, file);
            return File.Exists(path) ? path : null;
        }

        /// <summary>The host's, always: the interface the component implements must be the host's own type.</summary>
        private static bool IsHostAssembly(string? name) =>
            name is null
            || name.Equals("ClaudeLauncher.Infrastructure", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ClaudeLauncher.Core", StringComparison.OrdinalIgnoreCase);
    }
}
