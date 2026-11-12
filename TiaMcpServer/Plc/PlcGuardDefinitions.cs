using TiaMcpServer.Safety.Pipeline;

namespace TiaMcpServer.Plc;

/// <summary>The plc_write guards (spec §4.2). Only block and info severities exist: nothing asks the user.</summary>
public static class PlcGuardDefinitions
{
    public const string StateUnverifiable = "plc_state_unverifiable";
    public const string DeletesBlock = "plc_deletes_block";
    public const string DeletesGroupContents = "plc_deletes_group_contents";
    public const string DeletesTableContents = "plc_deletes_table_contents";
    public const string AddressOverlap = "plc_address_overlap";
    public const string ObSingletonExists = "plc_ob_singleton_exists";

    /// <summary>Produktiv only: a fail-safe block is never changed through this server.</summary>
    public const string SafetyBlock = "plc_safety_block";

    /// <summary>Produktiv only: a delete that could not be taken back is refused until its container is empty.</summary>
    public const string DeleteNotEmpty = "plc_delete_not_empty";

    public static IReadOnlyList<WriteGuardDefinition> Definitions { get; } = WriteGuardCatalog.NonInteractive(new[]
    {
        new WriteGuardDefinition(StateUnverifiable, WriteGuardSeverities.Block, "The PLC inventory is incomplete, the update or delete target or what a delete removes is unreadable, the user-constant value an update replaces is unreadable, or the content cannot be exported; unreadable evidence is never treated as empty."),
        new WriteGuardDefinition(ObSingletonExists, WriteGuardSeverities.Block, "create_block targets a singleton OB event class whose number an existing or in-call OB already holds."),
        new WriteGuardDefinition(SafetyBlock, WriteGuardSeverities.Block, "The target is a fail-safe block (F_LAD, F_FBD, F_DB and so on). Design, validation and acceptance of safety programs stay with a person in TIA Portal."),
        new WriteGuardDefinition(DeleteNotEmpty, WriteGuardSeverities.Block, "The block group or tag table is not empty. Delete its content item by item first, so that each delete can be taken back."),
        new WriteGuardDefinition(DeletesBlock, WriteGuardSeverities.Info, "Deleting the block removes it and its content."),
        new WriteGuardDefinition(DeletesGroupContents, WriteGuardSeverities.Info, "Deleting the block group removes every block and group inside it."),
        new WriteGuardDefinition(DeletesTableContents, WriteGuardSeverities.Info, "Deleting the tag table removes its tags and user constants."),
        new WriteGuardDefinition(AddressOverlap, WriteGuardSeverities.Info, "The requested logical address is already used by another tag; TIA Portal allows the overlap."),
    }, "PLC");
}
