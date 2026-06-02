using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Google.GenAI;

namespace Demo;

// ---------------------------------------------------------------------------
// Globals: a Roslyn szkript ezen keresztül fér hozzá az élő ablakhoz
// ---------------------------------------------------------------------------
public class ScriptGlobals
{
    public MainWindow Window { get; set; } = null!;

    public string DisplayText
    {
        get => Window.Display.Text ?? "";
        set => Dispatcher.UIThread.Post(() => Window.Display.Text = value);
    }
}

public partial class MainWindow : Window
{
    // Roslyn "memória" – az összes korábbi szkript állapota megmarad
    private ScriptState<object>? _scriptState;
    private readonly ScriptGlobals _globals;
    private readonly SelfEvolver _evolver;

    // Futás közbeni (RAM-only) gombok nyilvántartása
    private readonly List<(string Label, string Code)> _runtimeFeatures = new();

    public MainWindow()
    {
        InitializeComponent();
        _globals = new ScriptGlobals { Window = this };
        _evolver = new SelfEvolver();
        LoadEnvironment();
        UpdateEvolverStatus();
    }

    // ---------------------------------------------------------------------------
    // .env betöltés
    // ---------------------------------------------------------------------------
    private void LoadEnvironment()
    {
        try
        {
            DotNetEnv.Env.Load();
            var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrEmpty(key))
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", key);

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")))
                SetStatus("⚠️ GOOGLE_API_KEY hiányzik a .env fájlból!", isError: true);
            else
                SetStatus("✅ Kész. Írj be kérést → 🤖 AI gomb.");
        }
        catch (Exception ex)
        {
            SetStatus($".env hiba: {ex.Message}", isError: true);
        }
    }

    private void UpdateEvolverStatus()
    {
        if (_evolver.IsSourceAvailable)
            EvolverStatusText.Text = "🟢 Self-evolving: AKTÍV (forráskód írható)";
        else
            EvolverStatusText.Text = "🟡 Self-evolving: RAM mód (forráskód nem elérhető)";
    }

    // ---------------------------------------------------------------------------
    // Statikus gombok
    // ---------------------------------------------------------------------------
    private void OnInputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var clearStates = new[] { "0", "Hiba", "Fordítási hiba", "API hiba" };
        if (clearStates.Contains(Display.Text)) Display.Text = "";
        Display.Text += button.Content?.ToString();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Display.Text = "0";
        SetStatus("Törölve.");
    }

    private async void OnCalculateClick(object? sender, RoutedEventArgs e)
    {
        var input = Display.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;
        try
        {
            var opts = ScriptOptions.Default.WithImports("System", "System.Math");
            var result = await CSharpScript.EvaluateAsync(input, opts);
            Display.Text = result?.ToString() ?? "null";
        }
        catch
        {
            Display.Text = "Hiba";
        }
    }

    // ---------------------------------------------------------------------------
    // 🤖 AI GOMB – a self-evolving mag
    // ---------------------------------------------------------------------------
    private async void OnAiClick(object? sender, RoutedEventArgs e)
    {
        var prompt = Display.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt == "0")
        {
            SetStatus("Írj be kérést a kijelzőre!", isError: true);
            return;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")))
        {
            SetStatus("Hiányzó API kulcs!", isError: true);
            return;
        }

        AiButton.IsEnabled = false;
        SetStatus("🤖 AI generálja a kódot...");

        try
        {
            // AI hívás – JSON választ kérünk (metódus + gomb label)
            var aiResponse = await CallGeminiForEvolveAsync(prompt);

            // 1. Azonnali futtatás RAM-ban (nem kell újraindítás az előnézethez)
            await RunCodeInRamAsync(aiResponse.Label, aiResponse.RuntimeScript);

            // 2. Ha van forráskód → írja be magát és fordítsa újra
            if (_evolver.IsSourceAvailable)
            {
                SetStatus("💾 Forráskódba írás és újrafordítás...");
                var result = await _evolver.EvolveAndRestartAsync(
                    featureName:      aiResponse.HandlerName,
                    buttonLabel:      aiResponse.Label,
                    handlerCode:      aiResponse.HandlerMethod,
                    progressCallback: msg => SetStatus(msg)
                );

                if (!result.Success)
                    SetStatus($"⚠️ Forráskód írás sikertelen (RAM mód marad): {result.Message}", isError: true);
                // Ha sikeres: EvolveAndRestartAsync újraindítja az appot → ide nem jutunk el
            }
            else
            {
                SetStatus($"✅ '{aiResponse.Label}' hozzáadva (RAM mód – újraindítás után elveszik).");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"❌ Hiba: {ex.Message}", isError: true);
        }
        finally
        {
            AiButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------------------
    // Gemini hívás – strukturált JSON választ kérünk
    // ---------------------------------------------------------------------------
    private async Task<AiEvolveResponse> CallGeminiForEvolveAsync(string userPrompt)
    {
        // FONTOS: a system prompt két dolgot kér egyszerre:
        //   handlerMethod = valódi C# partial class metódus (forráskódba kerül)
        //   runtimeScript = Roslyn szkript (azonnal fut, preview)
        string systemInstruction = """
            You are a senior C# / Avalonia UI developer building a self-evolving desktop calculator app.

            When given a feature request, return ONLY a JSON object with these exact fields:
            {
              "label": "max 10 char button label, e.g. '√x' or 'Base64' or 'Idő'",
              "handlerName": "OnSqrtClick",
              "handlerMethod": "private void OnSqrtClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)\n{\n    if (!double.TryParse(Display.Text, out double val)) { Display.Text = \"Hiba\"; return; }\n    Display.Text = Math.Sqrt(val).ToString(\"G6\");\n}",
              "runtimeScript": "if (double.TryParse(DisplayText, out double v)) DisplayText = Math.Sqrt(v).ToString(\"G6\"); else DisplayText = \"Hiba\";"
            }

            handlerMethod rules:
            - Complete private C# method body (will be injected into MainWindow partial class)
            - Access the display via: Display.Text  (TextBox named Display)
            - No class or namespace wrapper – just the method
            - Can use System, System.Math, System.Linq, System.Net.Http, System.Threading.Tasks
            - For async operations: use async void + await
            - handlerName must be unique PascalCase ending with "Click"

            runtimeScript rules:
            - Plain Roslyn script (NOT wrapped in a method)
            - Access display via DisplayText property (string, readable and writable)
            - Should demonstrate the feature immediately
            - Must be a few lines max, self-contained

            CRITICAL: Output ONLY valid JSON. No markdown, no ```json fences, no extra text.
            """;

        var client = new Client();
        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: $"{systemInstruction}\n\nFEATURE REQUEST: {userPrompt}"
        );

        var raw = response.Candidates?[0].Content?.Parts?[0].Text ?? "";

        // Esetleges markdown blokk eltávolítása
        raw = raw
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();

        var parsed = JsonSerializer.Deserialize<AiEvolveResponse>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new Exception("Nem sikerült értelmezni az AI JSON válaszát.");

        return parsed;
    }

    // ---------------------------------------------------------------------------
    // Azonnali RAM-futtatás (preview, nincs újraindítás szükséges)
    // ---------------------------------------------------------------------------
    private async Task RunCodeInRamAsync(string label, string runtimeScript)
    {
        SetStatus($"⚡ '{label}' futtatása RAM-ban (preview)...");

        var options = BuildScriptOptions();

        try
        {
            if (_scriptState == null)
            {
                _scriptState = await CSharpScript.RunAsync(
                    runtimeScript, options,
                    globals: _globals,
                    globalsType: typeof(ScriptGlobals));
            }
            else
            {
                _scriptState = await _scriptState.ContinueWithAsync(runtimeScript);
            }

            // Gomb hozzáadása a DynamicButtonPanel-hez (RAM-only)
            await Dispatcher.UIThread.InvokeAsync(() =>
                AddRuntimeButton(label, runtimeScript));

            _runtimeFeatures.Add((label, runtimeScript));
        }
        catch (CompilationErrorException ex)
        {
            var errors = string.Join("; ", ex.Diagnostics.Select(d => d.GetMessage()));
            Display.Text = "Fordítási hiba";
            SetStatus($"❌ Roslyn hiba: {errors}", isError: true);
        }
    }

    // ---------------------------------------------------------------------------
    // RAM-only gomb a DynamicButtonPanel-hez
    // ---------------------------------------------------------------------------
    private void AddRuntimeButton(string label, string code)
    {
        var btn = new Button
        {
            Content      = label,
            FontSize     = 13,
            Padding      = new Thickness(10, 6),
            Margin       = new Thickness(3),
            Background   = new SolidColorBrush(Color.Parse("#2979FF")),
            Foreground   = Brushes.White,
            CornerRadius = new CornerRadius(4),
            Tag          = code
        };

        btn.Click += async (s, _) =>
        {
            if (s is not Button b) return;
            b.IsEnabled = false;
            try
            {
                var opts = BuildScriptOptions();
                if (_scriptState == null)
                    _scriptState = await CSharpScript.RunAsync(
                        b.Tag as string ?? "", opts,
                        globals: _globals, globalsType: typeof(ScriptGlobals));
                else
                    _scriptState = await _scriptState.ContinueWithAsync(b.Tag as string ?? "");
            }
            catch (Exception ex)
            {
                SetStatus($"❌ Hiba: {ex.Message}", isError: true);
            }
            finally { b.IsEnabled = true; }
        };

        var shortCode = code.Length > 150 ? code[..150] + "..." : code;
        ToolTip.SetTip(btn, $"⚠️ RAM-only (újraindítás után elveszik)\n\nKód:\n{shortCode}");
        DynamicButtonPanel.Children.Add(btn);
    }

    // ---------------------------------------------------------------------------
    // Roslyn ScriptOptions (közös)
    // ---------------------------------------------------------------------------
    private static ScriptOptions BuildScriptOptions() =>
        ScriptOptions.Default
            .WithReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
                typeof(Window).Assembly,
                typeof(Avalonia.Media.Color).Assembly,
                Assembly.GetExecutingAssembly()
            )
            .WithImports(
                "System", "System.Math", "System.Linq",
                "System.Collections.Generic", "System.Net.Http",
                "System.Threading.Tasks", "Avalonia.Controls", "Avalonia.Media"
            );

    // ---------------------------------------------------------------------------
    // Státuszsor
    // ---------------------------------------------------------------------------
    private void SetStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = isError
                ? new SolidColorBrush(Color.Parse("#D32F2F"))
                : new SolidColorBrush(Color.Parse("#555555"));
        });
    }

    // [INJECT POINT]
}

// ---------------------------------------------------------------------------
// AI válasz modell
// ---------------------------------------------------------------------------
public record AiEvolveResponse(
    string Label,
    string HandlerName,
    string HandlerMethod,
    string RuntimeScript
);