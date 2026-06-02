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

    public async Task<EvolveResult> EvolveAndRestartAsync(
        string featureName,
        string buttonLabel,
        string handlerCode,
        Action<string> progressCallback)
    {
        try
        {
            progressCallback("📄 Forráskód beolvasása...");
            var csSource   = await File.ReadAllTextAsync(_csFilePath, Encoding.UTF8);
            var axamlSource = await File.ReadAllTextAsync(_axamlFilePath, Encoding.UTF8);

            if (csSource.Contains($"void {featureName}(") || csSource.Contains($"async void {featureName}("))
                return EvolveResult.Fail($"A '{featureName}' már létezik!");

            progressCallback("✏️ Kód injektálása...");
            var newCsSource = InjectMethodIntoCs(csSource, handlerCode);
            if (newCsSource == null) return EvolveResult.Fail("Nem találom a // [INJECT POINT] jelölőt a .cs fájlban.");

            progressCallback("🎨 Gomb injektálása...");
            var newAxamlSource = InjectButtonIntoAxaml(axamlSource, featureName, buttonLabel);
            if (newAxamlSource == null) return EvolveResult.Fail("Nem találom az <!-- [INJECT BUTTON] --> jelölőt az AXAML fájlban.");

            progressCallback("💾 Mentés...");
            await File.WriteAllTextAsync(_csFilePath + ".bak", csSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_axamlFilePath + ".bak", axamlSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_csFilePath, newCsSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_axamlFilePath, newAxamlSource, Encoding.UTF8);

            progressCallback("🔨 Fordítás (dotnet build)...");
            var buildResult = await RunDotnetBuildAsync(_projectRoot);
            if (!buildResult.Success)
            {
                progressCallback("⚠️ Fordítási hiba! Visszaállítás...");
                await File.WriteAllTextAsync(_csFilePath, csSource, Encoding.UTF8);
                await File.WriteAllTextAsync(_axamlFilePath, axamlSource, Encoding.UTF8);
                return EvolveResult.Fail($"Build hiba:\n{buildResult.Output}");
            }

            progressCallback("🚀 Újraindítás...");
            RestartApplication();

            return EvolveResult.Ok("Sikeres! Újraindul...");
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
                    Margin="2"/>
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
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) output.AppendLine("[ERR] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return (process.ExitCode == 0, output.ToString());
    }

    // FIGYELEM: Ez a rész változott! Levettük a 'static'-ot és 'dotnet run'-t használunk!
    private void RestartApplication()
    {
        // A legbiztosabb módja az újraindításnak, ha ugyanúgy "dotnet run"-t hívunk
        // a projekt könyvtárában, mintha te tennéd manuálisan a terminálból.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run",
            WorkingDirectory = _projectRoot, // Itt használjuk a projekt elérési útját
            UseShellExecute = true
        };

        Process.Start(psi);
        
        // Hagyunk fél másodpercet, hogy az új folyamat biztonságosan leváljon
        System.Threading.Thread.Sleep(500);
        Environment.Exit(0);
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