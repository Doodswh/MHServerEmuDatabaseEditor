namespace MHDBEditor.ArchiveFormat
{
    // Mirrors MHServerEmu.Games.Properties.PropertyDataType exactly, from Property.cs.
    // These exact ordinals are what PropertyStore.Serialize() switches on to decide how many
    // bytes a property's value occupies - get them wrong and every property after the first
    // mismatch desyncs. Confirmed against the actual source, not guessed.
    public enum PropertyDataType
    {
        Boolean,
        Real,
        Integer,
        Prototype,
        Curve,
        Asset,
        EntityId,
        Time,
        Guid,
        RegionId,
        Int21Vector3,
        Invalid = -1,
    }

    // Mirrors MHServerEmu.Games.Properties.PropertyParamType exactly, from Property.cs.
    public enum PropertyParamType
    {
        Invalid = -1,
        Integer = 0,
        Asset = 1,
        Prototype = 2
    }
}
