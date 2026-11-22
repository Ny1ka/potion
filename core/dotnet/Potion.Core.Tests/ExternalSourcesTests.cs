#if DEBUG
using System.Text.Json;
using Potion.Core.Dropper;
using Xunit;

/// <summary>The Debug-only external source extension point, with a script standing in for an external program.</summary>
public sealed class ExternalSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"potion-ext-{Guid.NewGuid():N}");
    private string Sources => Path.Combine(_root, "sources");
    private static readonly bool Windows = OperatingSystem.IsWindows();

    public ExternalSourcesTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private static DropLink Link(string text) => SourceRegistry.Shared.Parse(text).Link!;

    /// <summary>A source folder whose program is the script (sh on macOS/Linux, PowerShell on Windows).</summary>
    private void MakeSource(string name, string[] hosts, string unixScript, string powershellScript)
    {
        var folder = Path.Combine(Sources, name);
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, Windows ? "run.ps1" : "run.sh");
        File.WriteAllText(script, Windows ? powershellScript : unixScript);
        var manifest = new Dictionary<string, object>
        {
            ["version"] = 1, ["id"] = name, ["name"] = char.ToUpperInvariant(name[0]) + name[1..], ["hosts"] = hosts,
            ["executable"] = Windows ? "pwsh" : "/bin/sh",
            ["arguments"] = Windows ? new[] { "-NoProfile", "-NonInteractive", "-File", script } : new[] { script },
        };
        File.WriteAllText(Path.Combine(folder, "source.json"), JsonSerializer.Serialize(manifest));
    }

    private const string WavSh = """
        out="$3"
        printf '{"progress":0.5}\n'
        printf '{"title":"Clip one"}\n'
        printf 'RIFF\044\000\000\000WAVEfmt ' > "$out/clip.wav"
        printf '{"file":"%s/clip.wav"}\n' "$out"
        """;

    private const string WavPs = """
        param($command, $link, $out)
        Write-Output '{"progress":0.5}'
        Write-Output '{"title":"Clip one"}'
        $path = Join-Path $out 'clip.wav'
        $bytes = [byte[]](0x52,0x49,0x46,0x46,0x24,0,0,0) + [Text.Encoding]::ASCII.GetBytes('WAVEfmt ')
        [IO.File]::WriteAllBytes($path, $bytes)
        Write-Output ('{"file":' + (ConvertTo-Json $path) + '}')
        """;

    [Fact]
    public void LoadsValidSourcesAndMatchesHosts()
    {
        MakeSource("alpha", ["example.org"], WavSh, WavPs);
        Directory.CreateDirectory(Path.Combine(Sources, "broken"));
        File.WriteAllText(Path.Combine(Sources, "broken", "source.json"), "not json");

        var sources = ExternalSources.Load(Sources);
        Assert.Equal(["alpha"], sources.Select(s => s.Id));
        Assert.True(sources[0].Handles(Link("https://example.org/watch?v=1")));
        Assert.True(sources[0].Handles(Link("https://media.example.org/a")));
        Assert.False(sources[0].Handles(Link("https://notexample.org/a")));
        Assert.Empty(ExternalSources.Load(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task JobUsesTheExternalSourceThenConverts()
    {
        MakeSource("alpha", ["example.org"], WavSh, WavPs);
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        var converter = new FakeConverter();
        var runner = new DropJobRunner(new Downloader(new ScriptedTransport(), "1"), converter, today: () => "2026-11-22")
        {
            ExternalSources = ExternalSources.Load(Sources),
        };
        var outcome = await runner.RunAsync(Link("https://example.org/watch?v=1"), output, new DropOptions("mp3"));
        Assert.Equal("wav", outcome.SourceFormatId);
        Assert.Equal(["Clip one.mp3"], outcome.Files.Select(Path.GetFileName));
        Assert.Equal("wav", converter.Conversions[0].From);
        Assert.Equal(["Clip one.mp3"], Directory.EnumerateFileSystemEntries(output).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ErrorsComeBackInTheSourcesOwnWords()
    {
        MakeSource("failing", ["example.org"], "echo 'Something went wrong upstream' >&2\nexit 3\n",
            "[Console]::Error.WriteLine('Something went wrong upstream')\nexit 3\n");
        var outside = Path.Combine(_root, "outside.wav");
        MakeSource("escaping", ["example.net"], $"printf 'x' > '{outside}'\nprintf '{{\"file\":\"{outside}\"}}\\n'\n",
            $"Set-Content -Path '{outside}' -Value 'x'\nWrite-Output ('{{\"file\":' + (ConvertTo-Json '{outside}') + '}}')\n");
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        var runner = new DropJobRunner(new Downloader(new ScriptedTransport(), "1"), new FakeConverter()) { ExternalSources = ExternalSources.Load(Sources) };

        var a = await Assert.ThrowsAsync<DropException>(() => runner.RunAsync(Link("https://example.org/a"), output, new DropOptions()));
        Assert.Equal("Something went wrong upstream", a.Message);
        Assert.False(a.TooLong);
        var b = await Assert.ThrowsAsync<DropException>(() => runner.RunAsync(Link("https://example.net/a"), output, new DropOptions()));
        Assert.Equal("Escaping didn't save its file in the download folder.", b.Message);
    }
}
#endif
