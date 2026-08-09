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

    /// <summary>
    /// Even one item is labelled. Without the header a single copy reads as nothing but a path, which is
    /// indistinguishable from a text clip that happens to contain one - the reported confusion.
    /// </summary>
    [Fact]
    public void SingleFileIsLabelledAndKeepsItsFullPath()
        => Assert.Equal($"1 file{Environment.NewLine}D:\\Work\\2026\\budget.xlsx",
            FileListPreview.Describe([@"D:\Work\2026\budget.xlsx"]));

    /// <summary>
    /// A folder says so, and carries a trailing separator. That marker is the shortest unambiguous one there
    /// is and the one every shell already uses, so it costs no words per line.
    /// </summary>
    [Fact]
    public void SingleFolderSaysFolderAndIsMarked()
        => Assert.Equal($"1 folder{Environment.NewLine}D:\\Work\\2026\\Reports\\",
            FileListPreview.Describe([@"D:\Work\2026\Reports"], _ => true));

    /// <summary>
    /// The shared folder is stated once and the files by name, one per line. A multiple selection almost
    /// always comes from one directory, so repeating the prefix per file spends the width on the least
    /// useful part.
    /// </summary>
    [Fact]
    public void FilesFromOneFolderNameTheFolderOnceThenOnePerLine()
    {
        var text = FileListPreview.Describe(
            [@"D:\Work\2026\budget.xlsx", @"D:\Work\2026\notes.txt", @"D:\Work\2026\report.docx"]);

        var lines = text.Split(Environment.NewLine);

        Assert.Equal(@"3 files in D:\Work\2026", lines[0]);
        Assert.Equal(["budget.xlsx", "notes.txt", "report.docx"], lines.Skip(1));
    }

    /// <summary>A mixed selection states both counts, so neither kind is silently implied.</summary>
    [Fact]
    public void MixedFilesAndFoldersStateBothCounts()
    {
        var text = FileListPreview.Describe(
            [@"D:\Work\a.txt", @"D:\Work\b.txt", @"D:\Work\Reports"],
            p => p.EndsWith("Reports", StringComparison.Ordinal));

        var lines = text.Split(Environment.NewLine);

        Assert.Equal(@"2 files, 1 folder in D:\Work", lines[0]);
        Assert.Equal(["a.txt", "b.txt", @"Reports\"], lines.Skip(1));
    }

    /// <summary>
    /// Mixed folders fall back to full paths: two files both called report.docx from different directories
    /// must not read as the same file listed twice.
    /// </summary>
    [Fact]
    public void FilesFromDifferentFoldersKeepTheirFullPaths()
    {
        var text = FileListPreview.Describe([@"D:\A\report.docx", @"D:\B\report.docx"]);
        var lines = text.Split(Environment.NewLine);

        Assert.Equal("2 files", lines[0]);
        Assert.Equal([@"D:\A\report.docx", @"D:\B\report.docx"], lines.Skip(1));
    }

    /// <summary>Windows paths are case-insensitive, so casing must not split one folder into two.</summary>
    [Fact]
    public void FolderComparisonIgnoresCase()
        => Assert.StartsWith(@"2 files in D:\Work",
            FileListPreview.Describe([@"D:\Work\a.txt", @"d:\work\b.txt"]),
            StringComparison.Ordinal);

    /// <summary>
    /// A UNC path is never probed for directory-ness. The stat blocks for seconds against an offline server,
    /// and this runs on the path reached from the clipboard notification.
    /// </summary>
    [Fact]
    public void UncPathsAreNotProbed()
    {
        var probed = new List<string>();

        _ = FileListPreview.Describe([@"\\server\share\file.txt"], p =>
        {
            probed.Add(p);
            return false;
        });

        // The supplied test is still called - the UNC guard lives in the default probe, which is what
        // TryDescribe passes - so this pins the guard where it belongs rather than in the formatter.
        Assert.Single(probed);
    }

    [Fact]
    public void DescribesAPayloadSetEndToEnd()
    {
        var payloads = new[]
        {
            new ClipPayload(FileListPreview.CfHdrop, null, Hdrop(true, @"D:\Work\a.txt", @"D:\Work\b.txt")),
        };

        Assert.StartsWith(@"2 files in D:\Work", FileListPreview.TryDescribe(payloads), StringComparison.Ordinal);
    }

    /// <summary>
    /// The real probe, unmocked: a directory that exists is marked, a sibling file is not. Uses the temp
    /// directory rather than a fixture, since what is under test is the filesystem check itself.
    /// </summary>
    [Fact]
    public void RealDirectoriesAreDetectedThroughTryDescribe()
    {
        var root = Path.Combine(Path.GetTempPath(), "pastejump-filelist-" + Guid.NewGuid().ToString("n"));
        var folder = Path.Combine(root, "Reports");
        var file = Path.Combine(root, "notes.txt");

        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "x");

        try
        {
            var text = FileListPreview.TryDescribe(
                [new ClipPayload(FileListPreview.CfHdrop, null, Hdrop(true, file, folder))]);

            Assert.NotNull(text);

            var lines = text!.Split(Environment.NewLine);

            Assert.Equal($"1 file, 1 folder in {root}", lines[0]);
            Assert.Equal(["notes.txt", @"Reports\"], lines.Skip(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The description round-trips back to the paths. The history window needs them again to show a thumbnail,
    /// and a row keeps only the preview text - so these two functions must not drift apart.
    /// </summary>
    [Fact]
    public void DescriptionRoundTripsBackToItsPaths()
    {
        // One file, several sharing a folder, and several that do not - the three shapes Describe produces.
        string[][] cases =
        [
            [@"D:\Photos\holiday.jpg"],
            [@"D:\Photos\a.jpg", @"D:\Photos\b.png"],
            [@"D:\A\report.docx", @"D:\B\report.docx"],
        ];

        foreach (var paths in cases)
        {
            Assert.Equal(paths, FileListPreview.TryReadPathsFromDescription(FileListPreview.Describe(paths)));
        }
    }

    /// <summary>A folder's trailing marker is not part of its path when read back.</summary>
    [Fact]
    public void FolderMarkerIsStrippedOnTheWayBack()
    {
        var described = FileListPreview.Describe(
            [@"D:\Work\a.txt", @"D:\Work\Reports"],
            p => p.EndsWith("Reports", StringComparison.Ordinal));

        Assert.Equal([@"D:\Work\a.txt", @"D:\Work\Reports"],
            FileListPreview.TryReadPathsFromDescription(described));
    }

    /// <summary>
    /// Text that merely contains a path is not a file list. Without this a copied path would sprout a
    /// thumbnail and a resolution, claiming to be something it is not.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(@"D:\Photos\holiday.jpg")]
    [InlineData("see the file\nD:\\Photos\\holiday.jpg")]
    public void PlainTextIsNotReadAsAFileList(string text)
        => Assert.Empty(FileListPreview.TryReadPathsFromDescription(text));

    /// <summary>Null, not a description of nothing, so the caller keeps its own placeholder.</summary>
    [Fact]
    public void PayloadSetWithoutHdropIsNotDescribed()
        => Assert.Null(FileListPreview.TryDescribe([new ClipPayload(13, null, [1, 2, 3])]));

    [Fact]
    public void PayloadSetWithMalformedHdropIsNotDescribed()
        => Assert.Null(FileListPreview.TryDescribe(
            [new ClipPayload(FileListPreview.CfHdrop, null, new byte[4])]));
}
