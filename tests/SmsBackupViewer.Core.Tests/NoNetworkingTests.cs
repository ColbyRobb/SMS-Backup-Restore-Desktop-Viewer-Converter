using System.Text.RegularExpressions;

namespace SmsBackupViewer.Core.Tests;

/// <summary>
/// Enforces the promise the project is built on: nothing here talks to a network.
///
/// The claim is worth proving rather than asserting. Backups hold private messages, and a user
/// deciding whether to trust an offline tool should be able to point at a test that fails if
/// networking is ever introduced.
/// </summary>
public class NoNetworkingTests
{
    /// <summary>Namespaces and types that would mean the code can reach the network.</summary>
    private static readonly string[] Forbidden =
    [
        @"\busing\s+System\.Net\b",
        @"\bSystem\.Net\.",
        @"\bHttpClient\b",
        @"\bWebClient\b",
        @"\bWebRequest\b",
        @"\bHttpWebRequest\b",
        @"\bTcpClient\b",
        @"\bUdpClient\b",
        @"\bSocket\b",
        @"\bDns\.",
        @"\bSmtpClient\b",
        @"\bClientWebSocket\b",
        @"\bIPAddress\b",
    ];

    private static string SourceRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "src")))
            directory = Path.GetDirectoryName(directory);

        Assert.NotNull(directory);
        return Path.Combine(directory!, "src");
    }

    [Fact]
    public void NoProjectReferencesNetworkingApis()
    {
        var offences = new List<string>();

        foreach (var file in Directory.GetFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            // Generated build artefacts are not hand-written source.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(file);
            foreach (var pattern in Forbidden)
            {
                var match = Regex.Match(text, pattern);
                if (!match.Success) continue;

                var line = text[..match.Index].Count(c => c == '\n') + 1;
                offences.Add($"{Path.GetFileName(file)}:{line} matches {pattern}");
            }
        }

        Assert.True(offences.Count == 0,
            "This project must not contain networking code. Found:\n  " + string.Join("\n  ", offences));
    }

    [Fact]
    public void NoProjectDeclaresANetworkingPackage()
    {
        var suspicious = new[] { "Http", "Rest", "Grpc", "WebSocket", "Azure", "AWS", "Telemetry", "AppInsights" };
        var offences = new List<string>();

        foreach (var file in Directory.GetFiles(SourceRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"PackageReference\s+Include=""([^""]+)"""))
            {
                var package = match.Groups[1].Value;
                if (suspicious.Any(s => package.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    offences.Add($"{Path.GetFileName(file)} references {package}");
            }
        }

        Assert.True(offences.Count == 0,
            "A dependency that could reach the network was added. Found:\n  " + string.Join("\n  ", offences));
    }
}
