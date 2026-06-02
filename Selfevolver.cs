using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Demo;

public class SelfEvolver
{
    private readonly string _projectRoot;
    private readonly string _csFilePath;
    private readonly string _axamlFilePath;

    public SelfEvolver()
    {
        _projectRoot = FindProjectRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Nem találom a projekt gyökerét (.csproj).");

        _csFilePath    = Path.Combine(_projectRoot, "MainWindow.axaml.cs");
        _axamlFilePath = Path.Combine(_projectRoot, "MainWindow.axaml");
    }

    public bool IsSourceAvailable => File.Exists(_csFilePath) && File.Exists(_axamlFilePath);

    // Evolve némán: Nincs újraindítás! Csak a fájlokat mentjük és buildeljük.
    public async Task<EvolveResult> EvolveSilentlyAsync(
        string featureName,
        string buttonLabel,
        string handlerCode)
    {
        try
        {
            var csSource   = await File.ReadAllTextAsync(_csFilePath, Encoding.UTF8);
            var axamlSource = await File.ReadAllTextAsync(_axamlFilePath, Encoding.UTF8);

            if (csSource.Contains($"void {featureName}(") || csSource.Contains($"async void {featureName}("))
                return EvolveResult.Fail($"A '{featureName}' már létezik!");

            var newCsSource = InjectMethodIntoCs(csSource, handlerCode);
            if (newCsSource == null) return EvolveResult.Fail("Nem találom a // [INJECT POINT] jelölőt.");

            var newAxamlSource = InjectButtonIntoAxaml(axamlSource, featureName, buttonLabel);
            if (newAxamlSource == null) return EvolveResult.Fail("Nem találom az <!-- [INJECT BUTTON] --> jelölőt.");

            await File.WriteAllTextAsync(_csFilePath + ".bak", csSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_axamlFilePath + ".bak", axamlSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_csFilePath, newCsSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_axamlFilePath, newAxamlSource, Encoding.UTF8);

            var buildResult = await RunDotnetBuildAsync(_projectRoot);
            if (!buildResult.Success)
            {
                await File.WriteAllTextAsync(_csFilePath, csSource, Encoding.UTF8);
                await File.WriteAllTextAsync(_axamlFilePath, axamlSource, Encoding.UTF8);
                return EvolveResult.Fail($"Build hiba, forráskód visszaállítva.");
            }

            // MINDEN SIKERES, NINCS ÚJRAINDÍTÁS
            return EvolveResult.Ok("Mentve és befordítva.");
        }
        catch (Exception ex)
        {
            return EvolveResult.Fail($"Hiba: {ex.Message}");
        }
    }

    private static string? InjectMethodIntoCs(string source, string handlerCode)
    {
        const string injectMarker = "// [INJECT POINT]";
        if (!source.Contains(injectMarker)) return null;

        var injected = $"""

    // --- AI által generált funkció ---
    {handlerCode.Replace("\n", "\n    ")}
    // --- vége ---

    {injectMarker}
""";
        return source.Replace(injectMarker, injected);
    }

    private static string? InjectButtonIntoAxaml(string source, string featureName, string buttonLabel)
    {
        const string injectMarker = "<!-- [INJECT BUTTON] -->";
        if (!source.Contains(injectMarker)) return null;

        var buttonXaml = $"""
            <Button Content="{buttonLabel}"
                    Click="{featureName}"
                    FontSize="20"
                    Height="52"
                    HorizontalAlignment="Stretch"
                    VerticalAlignment="Stretch"
                    Margin="2"
                    Background="#2979FF"
                    Foreground="White"/>
            {injectMarker}
""";
        return source.Replace(injectMarker, buttonXaml);
    }

    private static async Task<(bool Success, string Output)> RunDotnetBuildAsync(string projectRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = "build --configuration Debug --no-restore",
            WorkingDirectory       = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi) ?? throw new Exception("Nem indult a build.");
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, "");
    }

    private static string? FindProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (dir.GetFiles("*.csproj").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

public record EvolveResult(bool Success, string Message)
{
    public static EvolveResult Ok(string msg)   => new(true,  msg);
    public static EvolveResult Fail(string msg) => new(false, msg);
}