namespace DBVC.Core
{
    public enum HistoryChangedFileState
    {
        Added,
        Modified,
        Deleted
    }

    public class HistoryChangedFile
    {
        public HistoryChangedFileState State { get; set; }
        public string RelativePath { get; set; } = string.Empty;
    }
}
