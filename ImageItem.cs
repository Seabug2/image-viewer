namespace ImageViewer;
public sealed record ImageItem(string Name, string? FilePath, string? ArchivePath = null, string? ArchiveEntry = null);
