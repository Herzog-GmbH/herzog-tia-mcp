using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Safety.Pipeline;
using TiaMcpServer.Worker;

namespace TiaMcpServer.Plc;

[McpServerToolType]
public class PlcWriteTools
{
    [McpServerTool(Name = "plc_write", ReadOnly = false, Destructive = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(PlcGuardedWriteResponse))]
    [Description("Execute up to 50 ordered PLC software writes against one verified already-open project. Omitted dryRun or dryRun:false executes; use dryRun:true to preview effects, dependsOn and guards without mutation. No confirmation prompt is sent. Operations: update_block_logic (blockPath, content, expectedContentHash), update_type_content (typePath, content, expectedContentHash), create_tag_table/delete_tag_table (tableName), create_tag/update_tag/delete_tag, create_user_constant/update_user_constant/delete_user_constant (tableName, name), create_block (blockPath, blockType, language, obEventClass for OB), delete_block, create_block_group, delete_block_group (blockPath). expectedContentHash is the contentHash from a plc_read of the same object in the write's format; content changed since that read is rejected with state_changed. Later items may use objects created earlier in the same call. A duplicate singleton OB and unreadable evidence block the call before anything runs. Creates are not checked for collisions before mutation. A native EngineeringTargetInvocationException during a mutation worker method fails that item as engineering_rejected with the Siemens message in engineeringError; rejection behavior across namespaces is not guaranteed. A same-table update_tag rename collision was observed to be silently ignored; the rename is read back and fails that item as postcondition_failed before any other property changes. Phases: preview, applied, blocked, error. Stops on first failure, with no rollback or automatic replay; re-read affected objects with plc_read before retrying. This tool never saves, downloads, or controls a PLC. Prefer edits over content for update_block_logic and update_type_content: targeted replacements the server applies to a fresh export, so the rest of the object stays byte-identical. In the produktiv access mode each call also keeps a snapshot of what it replaces, compares the read-back with what was sent (protection.items[].importDifference), takes a block change back when it causes new compile errors (category rolled_back, messages in blockImportOutcome), refuses fail-safe blocks and deletes of non-empty groups or tag tables, and can be taken back with plc_undo.")]
    public static Task<CallToolResult> PlcWrite(
        OpennessWorkerClient workerClient,
        WriteExecution execution,
        [Description("Ordered PLC write operations.")] PlcOperationRequest[] operations,
        [Description("True previews effects and guards without mutation; omitted or false executes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => execution.RunAsync(new PlcWriteDomain(workerClient),
            new WriteCall<PlcOperationRequest>(
                operations ?? [], dryRun), cancellationToken: cancellationToken);
}
