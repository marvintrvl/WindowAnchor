using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class TitleParserTests
{
    [Theory]
    [InlineData("Code", "README.md - Untitled (Workspace) - Visual Studio Code", "README.md")]
    [InlineData("Code", "quarterly - notes.md - Visual Studio Code", "quarterly - notes.md")]
    [InlineData("Cursor", "Program.cs - WindowAnchor - Cursor", "Program.cs")]
    public void Editor_title_parser_removes_workspace_suffix_without_truncating_real_filename(
        string processName,
        string title,
        string expectedFile)
    {
        (string? filePath, int confidence) = TitleParser.ExtractFilePath(processName, title);

        Assert.Equal(expectedFile, filePath);
        Assert.Equal(40, confidence);
    }
}
