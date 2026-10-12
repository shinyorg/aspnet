using System.Text.Json.Serialization;

namespace Shiny.AspNet.FileSync.Internal;

// The JSON both ends exchange. A copy of the file Shiny.Net.HttpServer.FileSync.Client compiles in,
// kept identical apart from its namespace: the client talks to this server unchanged, so a field
// renamed here is a field the client stops seeing.

sealed record WireChunk(string Hash, long Size);

sealed record WireEntry(
    string Path,
    long Revision,
    long Size,
    string? Hash,
    DateTimeOffset Modified,
    bool Deleted,
    List<WireChunk>? Chunks
);

/// <summary><c>GET changes</c>: entries whose revision is after the cursor, oldest first.</summary>
sealed record WireChanges(List<WireEntry> Entries, long Cursor, bool HasMore);

/// <summary><c>GET changes/wait</c>: whether anything changed after the cursor before the timeout.</summary>
sealed record WireWait(bool Changes, long Cursor);

/// <summary>
/// <c>PUT files/manifest</c>: a new version of a file. <see cref="BaseRevision"/> is the revision the
/// client last saw; null means it believes the path does not exist (or was deleted).
/// </summary>
sealed record WireCommit(long? BaseRevision, long Size, string Hash, DateTimeOffset Modified, List<WireChunk> Chunks);

/// <summary>409 body: the version that is there now, so the client can decide what the conflict is.</summary>
sealed record WireConflict(WireEntry? Current);

/// <summary>A list of chunk hashes - the question and the answer of <c>POST chunks/missing</c>, and the 422 body of a commit.</summary>
sealed record WireHashes(List<string> Hashes);

sealed record WirePackResult(int Stored);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireChanges))]
[JsonSerializable(typeof(WireWait))]
[JsonSerializable(typeof(WireCommit))]
[JsonSerializable(typeof(WireConflict))]
[JsonSerializable(typeof(WireHashes))]
[JsonSerializable(typeof(WirePackResult))]
[JsonSerializable(typeof(WireEntry))]
sealed partial class WireJson : JsonSerializerContext;
