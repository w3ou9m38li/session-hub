using System.Collections.Concurrent;
using System.Text;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// The part of a transcript that is its conversation's own. All of it, as a rule; but a copy
/// that began with another conversation's history holds that history too, and what was said
/// there belongs to the other conversation:
/// <list type="bullet">
///   <item>a Claude Code session started by <c>--resume</c> carries the copied lines under the
///         old session's id, its own under its own (<see cref="Session"/>);</item>
///   <item>a Codex task forked from another starts with the other's history, written all at
///         the moment of the fork (<see cref="AfterFork"/>).</item>
/// </list>
/// Search, 「改动的文件」 and memory drafts read only this part, so nothing said once is counted
/// again in every copy.
/// </summary>
public readonly record struct OwnPart(string? Session, bool AfterFork)
{
    public static OwnPart Whole => default;

    public bool IsWhole => Session is null && !AfterFork;

    /// <summary>What the index stores to tell a change in how a transcript is read.</summary>
    public string? Stored => Session ?? (AfterFork ? ForkMark : null);

    private const string ForkMark = "@after-fork";

    /// <summary>A catalog row's own part: a resumed copy's lines, a fork's part after the fork.</summary>
    public static OwnPart For(SessionMetadata row) =>
        row.ResumedFrom is null ? Whole
        : row.Key.Backend == ConversationKey.Codex ? new OwnPart(null, true)
        : new OwnPart(row.Key.NativeSessionId, false);

    /// <summary>A reader of <paramref name="path"/>'s lines in file order, from <paramref name="fromOffset"/>, for this part.</summary>
    public LineFilter? Filter(string path, long fromOffset) => IsWhole ? null : new LineFilter(this, path, fromOffset);

    /// <summary>
    /// Decides, line by line in file order, whether a line is the conversation's own. A fork's
    /// copy is the block written with the rollout's first line — until the app marks the copy
    /// applied, or two seconds have passed. The time is taken from the first line itself, so it
    /// does not matter which lines a reader hands over; a read that starts past the file's
    /// beginning is past the copy.
    /// </summary>
    public sealed class LineFilter
    {
        private static readonly TimeSpan CopyWritten = TimeSpan.FromSeconds(2);

        private readonly byte[]? _session;
        private readonly DateTimeOffset _forkedAt;
        private bool _inCopy;

        internal LineFilter(OwnPart part, string path, long fromOffset)
        {
            _session = part.Session is null ? null : TranscriptReader.SessionMarker(part.Session);
            if (part.AfterFork && fromOffset == 0 && FirstLineTime(path) is { } forkedAt)
            {
                _forkedAt = forkedAt;
                _inCopy = true;
            }
            // Without a time to tell the copy by, everything counts.
        }

        public bool Keep(ReadOnlySpan<byte> line)
        {
            if (_session is not null) return line.IndexOf(_session) >= 0;
            if (!_inCopy) return true;
            if (line.IndexOf("\"type\":\"thread_settings_applied\""u8) >= 0)
            {
                _inCopy = false;
                return false;
            }
            if (Timestamp(line) is { } at && at - _forkedAt >= CopyWritten) _inCopy = false;
            return !_inCopy;
        }

        private static DateTimeOffset? FirstLineTime(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[256];
                var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                return Timestamp(buffer.AsSpan(0, length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The record's own time — Codex writes it first: <c>{"timestamp":"2026-04-26T02:03:39.123Z",…</c>.</summary>
        private static DateTimeOffset? Timestamp(ReadOnlySpan<byte> line)
        {
            var head = line[..Math.Min(line.Length, 256)];
            var key = "\"timestamp\":\""u8;
            var at = head.IndexOf(key);
            if (at < 0) return null;
            var value = head[(at + key.Length)..];
            var end = value.IndexOf((byte)'"');
            if (end <= 0) return null;
            return DateTimeOffset.TryParse(Encoding.ASCII.GetString(value[..end]), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var time)
                ? time : null;
        }
    }

    // ----------------------------------------------------------------- forks

    /// <summary>A rollout's first line never changes: what it says is kept for the rest of the run.</summary>
    private static readonly ConcurrentDictionary<string, string?> Forks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The task a Codex rollout was forked from — <c>forked_from_id</c> in its first line — or
    /// null. A task a thread spawned for itself (a subagent) is not a fork of it.
    /// </summary>
    public static string? ForkedFrom(string rolloutPath) => Forks.GetOrAdd(rolloutPath, ReadForkedFrom);

    private static string? ReadForkedFrom(string path)
    {
        try
        {
            using var stream = TranscriptFiles.OpenRead(path);
            var buffer = new byte[64 * 1024];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var head = buffer.AsSpan(0, length);
            var end = head.IndexOf((byte)'\n');
            if (end >= 0) head = head[..end];
            if (head.IndexOf("\"type\":\"session_meta\""u8) < 0) return null;
            // The key is the record's own: in a string value its quotes would be escaped.
            var key = "\"forked_from_id\":\""u8;
            var at = head.IndexOf(key);
            if (at < 0) return null;
            var value = head[(at + key.Length)..];
            var close = value.IndexOf((byte)'"');
            if (close <= 0 || close > 64) return null;
            var id = Encoding.ASCII.GetString(value[..close]);
            return Guid.TryParse(id, out _) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZstdSharp.ZstdException)
        {
            return null;
        }
    }
}
