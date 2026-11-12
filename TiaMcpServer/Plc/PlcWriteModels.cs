using System.Text.Json.Serialization;
using TiaMcpServer.OperationBatches;
using TiaMcpServer.Safety.Pipeline;

namespace TiaMcpServer.Plc;

/// <summary>
/// What one plc_write item does, or would do: the resolved target, current → requested field
/// changes, where a created object goes, what a delete removes, bounded content diff evidence, and
/// the earlier item it depends on.
/// </summary>
public sealed record PlcWriteEffect(
    string Operation,
    PlcTargetIdentity Target,
    IReadOnlyList<PlcFieldChange> Changes,
    string? Placement,
    PlcRemoval? Removes,
    PlcContentDiffEvidence? ContentDiff,
    string? DependsOn);

/// <summary>Kind is Tag, UserConstant, TagTable, Block, BlockGroup or Type; inapplicable members are null.</summary>
public sealed record PlcTargetIdentity(
    string Kind,
    string PlcName,
    string? DeviceName,
    string? FolderPath,
    string? TableName,
    string? Name,
    string? BlockPath,
    string? TypePath);

public sealed record PlcFieldChange(string Field, string? Current, string? Requested);

public sealed record PlcRemoval(
    int TagCount,
    int UserConstantCount,
    IReadOnlyList<string> Blocks,
    IReadOnlyList<string> Groups,
    string? BlockType,
    string? Language);

public sealed record PlcWriteEffectPresentation(string OperationId, PlcWriteEffect? Effect, StructuredOperationOmission? Omission);

/// <summary>The plc_write response document; <see cref="Omission"/> is set when diagnostics were compacted.</summary>
public sealed record PlcGuardedWriteResponse(string Tool, string ContractVersion, string Phase, bool Success,
    WriteToolError? Error, IReadOnlyList<string> Warnings, IReadOnlyList<WriteGuardReport> Guards,
    IReadOnlyList<PlcWriteEffectPresentation> Effects, StructuredOperationBatch? Batch, PlcWriteVerification? Verification,
    StructuredOperationOmission? Omission)
{
    /// <summary>The safety net of this call in the Produktiv profile; absent elsewhere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlcWriteProtectionReport? Protection { get; init; }
}

/// <summary>The plc_undo response: the document of its last write, plus the journal view. plc_write keeps its own schema.</summary>
public sealed record PlcUndoResponse(string Tool, string ContractVersion, string Phase, bool Success,
    WriteToolError? Error, IReadOnlyList<string> Warnings, IReadOnlyList<WriteGuardReport> Guards,
    IReadOnlyList<PlcWriteEffectPresentation> Effects, StructuredOperationBatch? Batch, PlcWriteVerification? Verification,
    StructuredOperationOmission? Omission)
{
    /// <summary>The safety net of the last write.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlcWriteProtectionReport? Protection { get; init; }

    /// <summary>With list: the calls that can be taken back, newest first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PlcUndoCallInfo>? OpenCalls { get; init; }

    /// <summary>The calls this request took back, newest first.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PlcUndoCallInfo>? TakenBack { get; init; }
}

/// <summary>One plc_write call in the journal. <see cref="EarlierSession"/> says it was made before this server started.</summary>
public sealed record PlcUndoCallInfo(string CallId, DateTimeOffset At, bool EarlierSession, IReadOnlyList<string> Operations, string? NotUndoableReason);

/// <summary>What the server did to protect one plc_write call.</summary>
public sealed record PlcWriteProtectionReport(string CallId, bool Undoable, IReadOnlyList<PlcItemProtection> Items);

/// <summary>
/// One item's protection. <see cref="SnapshotFile"/> holds the previous content.
/// <see cref="RollbackApplied"/> says the previous content was restored. <see cref="ImportDifference"/>
/// lists what TIA Portal changed beyond the submitted text. <see cref="NotUndoableReason"/> is set
/// when plc_undo cannot take this item back. Empty members stay on the wire as null: the output
/// schema lists every positional member as required, and MCP clients reject a response without them.
/// </summary>
public sealed record PlcItemProtection(
    string OperationId,
    string? SnapshotFile,
    bool RollbackApplied,
    string? Note,
    PlcContentDiffEvidence? ImportDifference,
    string? NotUndoableReason);

/// <summary>One item resolved against the working state: an effect with its guards, or a call-stopping error.</summary>
public sealed record PlcResolution(PlcWriteEffect? Effect, IReadOnlyList<FiredGuard> Guards, WriteToolError? Error);

/// <summary>
/// One post-write check of one attempted item. <see cref="Check"/> is <c>contentHash</c> (a content
/// item reports its new <see cref="ContentHash"/>), <c>trusted</c> (the Openness call completed
/// without rejection; nothing was read back) or <c>superseded</c>.
/// </summary>
public sealed record PlcOperationVerification(string OperationId, bool Success, string Check,
    string? Expected, string? Actual, string? ContentHash, string? Message);

public sealed record PlcWriteVerification(bool Success, IReadOnlyList<PlcOperationVerification> Operations,
    StructuredOperationOmission? Omission)
{
    /// <summary>ContentHash = Actual for "contentHash" entries, null otherwise.</summary>
    public static PlcWriteVerification From(WriteVerification shared)
        => new(shared.Success, shared.Operations.Select(o => new PlcOperationVerification(
            o.OperationId, o.Success, o.Check, o.Expected, o.Actual,
            o.Check == "contentHash" ? o.Actual : null, o.Message)).ToArray(), shared.Omission);
}
