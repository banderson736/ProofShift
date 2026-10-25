using ProofShift.DemoContracts;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class SafeDemoRunnerDiagnosticTests
{
    [Fact]
    public void ParserSupportsOldSanitizedFormat()
    {
        const string line = "Demo scenario failed unexpectedly (DirectoryNotFoundException). " +
            "Exception details were withheld to protect configured connection values.";

        Assert.True(SafeDemoRunnerDiagnostic.TryParseLine(line, out var diagnostic));
        Assert.Equal(line, diagnostic);
    }

    [Fact]
    public void FormatterAndParserPreserveSafeMethodLocation()
    {
        var line = SafeDemoRunnerDiagnostic.Format("DirectoryNotFoundException",
            ["ProofShift.DemoScenarios.PensionDemoScenario.ExecuteAsync"]);

        Assert.Contains(" at ProofShift.DemoScenarios.PensionDemoScenario.ExecuteAsync", line,
            StringComparison.Ordinal);
        Assert.True(SafeDemoRunnerDiagnostic.TryParseLine(line, out var diagnostic));
        Assert.Equal(line, diagnostic);
    }

    [Theory]
    [InlineData("Server=prod;Password=do-not-leak")]
    [InlineData("System.Exception: secret=do-not-leak")]
    [InlineData("   at System.IO.File.Open(String path)")]
    public void ParserRejectsArbitraryStderr(string line)
    {
        Assert.False(SafeDemoRunnerDiagnostic.TryParseLine(line, out var diagnostic));
        Assert.Empty(diagnostic);
    }

    [Fact]
    public void ParserRejectsMultilineDiagnosticAndStackTrace()
    {
        var safeLine = SafeDemoRunnerDiagnostic.Format("IOException",
            ["ProofShift.DemoScenarios.PensionDemoScenario.ExecuteAsync"]);
        var output = $"{safeLine}{Environment.NewLine} at System.IO.File.Open(String path)";

        Assert.False(SafeDemoRunnerDiagnostic.TryParseLine(output, out var diagnostic));
        Assert.Empty(diagnostic);
    }

    [Fact]
    public void FormatterNeverEmitsConnectionValuesOrStackText()
    {
        var line = SafeDemoRunnerDiagnostic.Format("DirectoryNotFoundException",
            ["ProofShift.DemoScenarios.PensionDemoScenario.ExecuteAsync",
             "Server=prod;Password=do-not-leak\n at System.IO.File.Open"]);

        Assert.DoesNotContain("Server=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("prod", line, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.NewLine, line, StringComparison.Ordinal);
        Assert.True(SafeDemoRunnerDiagnostic.TryParseLine(line, out _));
    }
}
