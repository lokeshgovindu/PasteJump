using System.Text;
using PasteJump.Core.Model;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Describing a file copy. These matter beyond cosmetics: the preview is what <c>history_fts</c> indexes, so
/// while every file copy read "[files]" a file name could never be searched for.
/// </summary>
public class FileListPreviewTests
{
    /// <summary>Builds a CF_HDROP payload the way Explorer does: DROPFILES, then double-null-terminated paths.</summary>
    private static byte[] Hdrop(bool wide, params string[] paths)
    {
        var listBytes = wide
            ? Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + '\0')) + '\0')
            : Encoding.ASCII.GetBytes(string.Concat(paths.Select(p => p + '\0')) + '\0');

        var bytes = new byte[20 + listBytes.Length];

        BitConverter.GetBytes(20).CopyTo(bytes, 0);                  // pFiles
        BitConverter.GetBytes(wide ? 1 : 0).CopyTo(bytes, 16);       // fWide
        listBytes.CopyTo(bytes, 20);

        return bytes;
    }

    [Fact]
    public void ReadsWidePaths()
    {
        var paths = FileListPreview.TryReadPaths(
            Hdrop(true, @"D:\Work\a.txt", @"D:\Work\b.txt", @"D:\Work\c.txt"));

        Assert.Equal([@"D:\Work\a.txt", @"D:\Work\b.txt", @"D:\Work\c.txt"], paths);
    }

    /// <summary>The ANSI form still exists in payloads replayed from older applications.</summary>
    [Fact]
    public void ReadsAnsiPaths()
        => Assert.Equal([@"C:\one.txt", @"C:\two.txt"],
            FileListPreview.TryReadPaths(Hdrop(false, @"C:\one.txt", @"C:\two.txt")));

    /// <summary>
    /// A malformed payload yields nothing rather than throwing. This runs on the capture path, which is
    /// reached from the clipboard notification, and a throw there would lose the copy outright.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(19)]
    public void TruncatedPayloadYieldsNoPaths(int length)
        => Assert.Empty(FileListPreview.TryReadPaths(new byte[length]));

    [Fact]
    public void OffsetPointingOutsideThePayloadYieldsNoPaths()
    {
        var bytes = Hdrop(true, @"D:\a.txt");
        BitConverter.GetBytes(9999).CopyTo(bytes, 0);

        Assert.Empty(FileListPreview.TryReadPaths(bytes));
    }

    [Fact]
    public void SingleFileIsDescribedByItsFullPath()
        => Assert.Equal(@"D:\Work\2026\budget.xlsx",
            FileListPreview.Describe([@"D:\Work\2026\budget.xlsx"]));

    /// <summary>
    /// The shared folder is stated once and the files by name. A multiple selection almost always comes from
    /// one directory, so repeating the prefix per file spends the whole width on the least useful part.
    /// </summary>
    [Fact]
    public void FilesFromOneFolderNameTheFolderOnceThenTheFiles()
    {
        var text = FileListPreview.Describe(
            [@"D:\Work\2026\budget.xlsx", @"D:\Work\2026\notes.txt", @"D:\Work\2026\report.docx"]);

        Assert.StartsWith(@"3 files in D:\Work\2026", text, StringComparison.Ordinal);
        Assert.Contains("budget.xlsx, notes.txt, report.docx", text, StringComparison.Ordinal);

        // Every name present, because this string is what search matches against.
        Assert.Contains("report.docx", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mixed folders fall back to full paths: two files both called report.docx from different directories
    /// must not read as the same file listed twice.
    /// </summary>
    [Fact]
    public void FilesFromDifferentFoldersKeepTheirFullPaths()
    {
        var text = FileListPreview.Describe([@"D:\A\report.docx", @"D:\B\report.docx"]);

        Assert.StartsWith("2 files", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\A\report.docx", text, StringComparison.Ordinal);
        Assert.Contains(@"D:\B\report.docx", text, StringComparison.Ordinal);
        Assert.DoesNotContain("files in", text, StringComparison.Ordinal);
    }

    /// <summary>Windows paths are case-insensitive, so casing must not split one folder into two.</summary>
    [Fact]
    public void FolderComparisonIgnoresCase()
        => Assert.StartsWith(@"2 files in D:\Work",
            FileListPreview.Describe([@"D:\Work\a.txt", @"d:\work\b.txt"]),
            StringComparison.Ordinal);

    [Fact]
    public void DescribesAPayloadSetEndToEnd()
    {
        var payloads = new[]
        {
            new ClipPayload(FileListPreview.CfHdrop, null, Hdrop(true, @"D:\Work\a.txt", @"D:\Work\b.txt")),
        };

        Assert.StartsWith(@"2 files in D:\Work", FileListPreview.TryDescribe(payloads), StringComparison.Ordinal);
    }

    /// <summary>Null, not a description of nothing, so the caller keeps its own placeholder.</summary>
    [Fact]
    public void PayloadSetWithoutHdropIsNotDescribed()
        => Assert.Null(FileListPreview.TryDescribe([new ClipPayload(13, null, [1, 2, 3])]));

    [Fact]
    public void PayloadSetWithMalformedHdropIsNotDescribed()
        => Assert.Null(FileListPreview.TryDescribe(
            [new ClipPayload(FileListPreview.CfHdrop, null, new byte[4])]));
}
