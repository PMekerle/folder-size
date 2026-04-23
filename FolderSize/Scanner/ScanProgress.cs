namespace FolderSize.Scanner;

public readonly record struct ScanProgress(long FilesScanned, long BytesScanned, string CurrentPath);
