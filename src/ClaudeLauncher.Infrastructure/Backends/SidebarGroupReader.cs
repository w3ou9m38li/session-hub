using System.Text.Json;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>Sidebar group membership for one account/organization scope.</summary>
public sealed record SidebarGroups(
    IReadOnlyDictionary<string, string> GroupNamesById,
    IReadOnlyDictionary<string, string> GroupIdBySessionId)
{
    public static readonly SidebarGroups Empty =
        new(new Dictionary<string, string>(), new Dictionary<string, string>());

    public bool IsEmpty => GroupIdBySessionId.Count == 0;

    /// <summary>Group name for a Desktop Code session id, or null when it is ungrouped.</summary>
    public string? NameFor(string sessionId) =>
        GroupIdBySessionId.TryGetValue(sessionId, out var gid)
        && GroupNamesById.TryGetValue(gid, out var name)
            ? name
            : null;
}

/// <summary>
/// Reads sidebar group membership from Claude's own preference file, read-only.
///
/// <para>
/// This is an <b>undocumented local preference cache</b>, not a public API. It is used only
/// to label rows; nothing depends on it, and a shape change degrades to "no group" rather
/// than failing. The file is never written — it belongs to Claude.
/// </para>
///
/// <para>Two limits that the UI must not hide:</para>
/// <list type="bullet">
///   <item>Only <c>code:</c> (Desktop Code) sessions carry an assignment. CLI transcripts
///         have no sidebar group at all.</item>
///   <item>Only groups that currently hold an assignment appear here. A group the user
///         created but left empty is not in this cache, so absence is not proof it is gone.</item>
/// </list>
/// </summary>
public static class SidebarGroupReader
{
    private const string AssignmentPrefix = "code:";

    public static SidebarGroups Read(string profileRoot)
    {
        if (string.IsNullOrWhiteSpace(profileRoot)) return SidebarGroups.Empty;

        var path = Path.Combine(profileRoot, "claude_desktop_config.json");
        if (!File.Exists(path)) return SidebarGroups.Empty;

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (!TryGet(doc.RootElement, "preferences", out var prefs)) return SidebarGroups.Empty;
            if (!TryGet(prefs, "epitaxyPrefs", out var epitaxy)) return SidebarGroups.Empty;
            if (!TryGet(epitaxy, "dframe-group-scopes", out var scopes)) return SidebarGroups.Empty;
            if (scopes.ValueKind != JsonValueKind.Object) return SidebarGroups.Empty;

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var assignments = new Dictionary<string, string>(StringComparer.Ordinal);

            // Scope key is "{account}/{organization}". Merging scopes is safe here because
            // the session ids they map are themselves scope-unique.
            foreach (var scope in scopes.EnumerateObject())
            {
                if (scope.Value.ValueKind != JsonValueKind.Object) continue;

                if (TryGet(scope.Value, "groups", out var groups)
                    && groups.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in groups.EnumerateArray())
                    {
                        if (g.ValueKind != JsonValueKind.Object) continue;
                        var id = Text(g, "id");
                        var name = Text(g, "name");
                        if (id is { Length: > 0 } && name is { Length: > 0 }) names[id] = name;
                    }
                }

                if (TryGet(scope.Value, "assignments", out var assigned)
                    && assigned.ValueKind == JsonValueKind.Object)
                {
                    foreach (var a in assigned.EnumerateObject())
                    {
                        if (a.Value.ValueKind != JsonValueKind.String) continue;
                        if (!a.Name.StartsWith(AssignmentPrefix, StringComparison.Ordinal)) continue;
                        var sessionId = a.Name[AssignmentPrefix.Length..];
                        if (sessionId.Length > 0) assignments[sessionId] = a.Value.GetString()!;
                    }
                }
            }

            return new SidebarGroups(names, assignments);
        }
        catch (JsonException)
        {
            // Shape drift after a Claude update: no labels, no crash.
            return SidebarGroups.Empty;
        }
        catch (IOException)
        {
            return SidebarGroups.Empty;
        }
    }

    private static bool TryGet(JsonElement parent, string name, out JsonElement value) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value)
            ? true
            : (value = default) is var _ && false;

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
