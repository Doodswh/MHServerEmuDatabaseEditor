namespace MHDBEditor.Models
{
    // A saved-off item, captured from an existing item's raw data so it can be handed out
    // again later without needing to touch any guids/hex - "Save As Template..." in the
    // advanced item browser creates these; Simple Mode's "Give Item" picks from them by name.
    public class ItemTemplate
    {
        public string DisplayName { get; set; }
        public long EntityProtoGuid { get; set; }
        public long InventoryProtoGuid { get; set; }
        public byte[] ArchiveData { get; set; }
    }
}
