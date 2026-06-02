using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Demo;

/// <summary>
/// A self-evolving mag.
/// Feladata:
///   1. Megtalálja a saját forráskód fájlokat (futás közben, development módban).
///   2. Az AI által generált metódust + AXAML elemet beilleszti a forrásba.
///   3. dotnet build-del lefordítja.
///   4. Újraindítja az alkalmazást az új binárissal.
/// </summary>
public class SelfEvolver
{
    // A forrásfájlok helye – development módban a projekt gyökerében vannak.
    // Release módban ez nem működik (ott nincs forráskód), ami szándékos.
    private readonly string _projectRoot;
    private readonly string _csFilePath;
    private readonly string _axamlFilePath;
    private readonly string _csprojFilePath;

    public SelfEvolver()
    {
        // AppContext.BaseDirectory: pl. bin/Debug/net10.0/
        // 3 szinttel feljebb: projekt gyökér
        _projectRoot = FindProjectRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "Nem találom a projekt gyökerét (.csproj fájl alapján). " +
                "Self-evolving mód csak development build-ben működik!");

        _csFilePath    = Path.Combine(_projectRoot, "MainWindow.axaml.cs");
        _axamlFilePath = Path.Combine(_projectRoot, "MainWindow.axaml");
        _csprojFilePath = Directory.GetFiles(_projectRoot, "*.csproj")[0];
    }

    // -----------------------------------------------------------------------
    // Nyilvános API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Visszaadja, hogy elérhető-e a forráskód (development mód).
    /// </summary>
    public bool IsSourceAvailable =>
        File.Exists(_csFilePath) && File.Exists(_axamlFilePath);

    /// <summary>
    /// Beilleszti az AI által generált funkciót a forráskódba,
    /// lefordítja, majd újraindítja az alkalmazást.
    /// </summary>
    /// <param name="featureName">Az eseménykezelő neve (pl. "OnSqrtClick")</param>
    /// <param name="buttonLabel">A gomb felirata (pl. "√x")</param>
    /// <param name="handlerCode">A teljes C# eseménykezelő metódus kódja</param>
    /// <param name="progressCallback">UI visszajelzés callback</param>
    public async Task<EvolveResult> EvolveAndRestartAsync(
        string featureName,
        string buttonLabel,
        string handlerCode,
        Action<string> progressCallback)
    {
        try
        {
            // 1. Forráskód beolvasása
            progressCallback("📄 Forráskód beolvasása...");
            var csSource   = await File.ReadAllTextAsync(_csFilePath,   Encoding.UTF8);
            var axamlSource = await File.ReadAllTextAsync(_axamlFilePath, Encoding.UTF8);

            // 2. Már létezik ez a funkció?
            if (csSource.Contains($"void {featureName}(") || csSource.Contains($"async void {featureName}("))
                return EvolveResult.Fail($"A '{featureName}' metódus már létezik a forráskódban!");

            // 3. Metódus injektálása a .cs fájlba
            progressCallback("✏️ Metódus injektálása a .cs fájlba...");
            var newCsSource = InjectMethodIntoCs(csSource, handlerCode);
            if (newCsSource == null)
                return EvolveResult.Fail("Nem találom az injektálási pontot a .cs fájlban! " +
                                         "Ellenőrizd, hogy megvan-e a // [INJECT POINT] jelölő.");

            // 4. Gomb injektálása az AXAML fájlba
            progressCallback("🎨 Gomb injektálása az AXAML fájlba...");
            var newAxamlSource = InjectButtonIntoAxaml(axamlSource, featureName, buttonLabel);
            if (newAxamlSource == null)
                return EvolveResult.Fail("Nem találom az AXAML injektálási pontot! " +
                                         "Ellenőrizd, hogy megvan-e a <!-- [INJECT BUTTON] --> jelölő.");

            // 5. Fájlok mentése (backup után!)
            progressCallback("💾 Forrásfájlok mentése...");
            await File.WriteAllTextAsync(_csFilePath   + ".bak", csSource,    Encoding.UTF8);
            await File.WriteAllTextAsync(_axamlFilePath + ".bak", axamlSource, Encoding.UTF8);
            await File.WriteAllTextAsync(_csFilePath,   newCsSource,    Encoding.UTF8);
            await File.WriteAllTextAsync(_axamlFilePath, newAxamlSource, Encoding.UTF8);

            // 6. dotnet build
            progressCallback("🔨 Fordítás (dotnet build)...");
            var buildResult = await RunDotnetBuildAsync(_projectRoot);
            if (!buildResult.Success)
            {
                // Rollback ha nem sikerült fordítani
                progressCallback("⚠️ Fordítási hiba! Visszaállítás...");
                await File.WriteAllTextAsync(_csFilePath,   csSource,    Encoding.UTF8);
                await File.WriteAllTextAsync(_axamlFilePath, axamlSource, Encoding.UTF8);
                return EvolveResult.Fail($"Build hiba:\n{buildResult.Output}");
            }

            // 7. Újraindítás
            progressCallback("🚀 Újraindítás az új binárissal...");
            RestartApplication();

            return EvolveResult.Ok("Sikeres! Az alkalmazás újraindul...");
        }
        catch (Exception ex)
        {
            return EvolveResult.Fail($"Váratlan hiba: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Metódus injektálása a .cs fájlba
    // -----------------------------------------------------------------------
    private static string? InjectMethodIntoCs(string source, string handlerCode)
    {
        const string injectMarker = "// [INJECT POINT]";
        if (!source.Contains(injectMarker)) return null;

        // A metóduskód elé és mögé szép formázás
        var injected = $"""

    // --- AI által generált funkció ---
    {handlerCode.Replace("\n", "\n    ")}
    // --- vége ---

    {injectMarker}
""";
        return source.Replace(injectMarker, injected);
    }

    // -----------------------------------------------------------------------
    // Gomb injektálása az AXAML fájlba
    // -----------------------------------------------------------------------
    private static string? InjectButtonIntoAxaml(string source, string featureName, string buttonLabel)
    {
        const string injectMarker = "<!-- [INJECT BUTTON] -->";
        if (!source.Contains(injectMarker)) return null;

        var buttonXaml = $"""
            <Button Content="{buttonLabel}"
                    Click="{featureName}"
                    FontSize="13"
                    HorizontalAlignment="Stretch"
                    VerticalAlignment="Stretch"
                    Margin="2"
                    Background="#2979FF"
                    Foreground="White"/>
            {injectMarker}
""";
        return source.Replace(injectMarker, buttonXaml);
    }

    // -----------------------------------------------------------------------
    // dotnet build futtatása
    // -----------------------------------------------------------------------
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

        using var process = Process.Start(psi)
            ?? throw new Exception("Nem sikerült elindítani a dotnet build folyamatot.");

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) output.AppendLine("[ERR] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        return (process.ExitCode == 0, output.ToString());
    }

    // -----------------------------------------------------------------------
    // Alkalmazás újraindítása
    // -----------------------------------------------------------------------
    private static void RestartApplication()
    {
        // Az aktuális executable elindítása + jelenlegi process bezárása
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new Exception("Nem található az exe útvonala.");

        Process.Start(new ProcessStartInfo
        {
            FileName        = exePath,
            UseShellExecute = true
        });

        // Kis késleltetés, hogy az új process elindulhasson
        System.Threading.Thread.Sleep(500);
        Environment.Exit(0);
    }

    // -----------------------------------------------------------------------
    // Projekt gyökér megkeresése felfelé haladva
    // -----------------------------------------------------------------------
    private static string? FindProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (dir.GetFiles("*.csproj").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>
/// Az evolúció eredménye.
/// </summary>
public record EvolveResult(bool Success, string Message)
{
    public static EvolveResult Ok(string msg)   => new(true,  msg);
    public static EvolveResult Fail(string msg) => new(false, msg);
}