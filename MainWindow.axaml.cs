using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Google.GenAI;

namespace Demo;

public partial class MainWindow : Window
{
    private readonly SelfEvolver _evolver;

    public MainWindow()
    {
        InitializeComponent();
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
            EvolverStatusText.Text = "🟢 Self-evolving: AKTÍV (Azonnali kódba írás)";
        else
            EvolverStatusText.Text = "🔴 Self-evolving: INAKTÍV (Forráskód nem elérhető)";
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

    // ROSLYN itt maradt: A dinamikus matematikai kifejezések kiértékelésére
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
        if (!_evolver.IsSourceAvailable)
        {
            SetStatus("A forráskód nem elérhető. Evolúció nem lehetséges.", isError: true);
            return;
        }

        AiButton.IsEnabled = false;
        SetStatus("🤖 AI generálja a kódot...");

        try
        {
            // AI hívás
            var aiResponse = await CallGeminiForEvolveAsync(prompt);

            // AZONNALI forráskódba írás és újrafordítás (Nincs RAM mód)
            SetStatus("💾 Forráskódba írás és újrafordítás...");
            var result = await _evolver.EvolveAndRestartAsync(
                featureName:      aiResponse.HandlerName,
                buttonLabel:      aiResponse.Label,
                handlerCode:      aiResponse.HandlerMethod,
                progressCallback: msg => SetStatus(msg)
            );

            if (!result.Success)
            {
                SetStatus($"⚠️ Hiba történt: {result.Message}", isError: true);
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
    // Gemini hívás – Az eredeti prompt marad
    // ---------------------------------------------------------------------------
    private async Task<AiEvolveResponse> CallGeminiForEvolveAsync(string userPrompt)
    {
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
        raw = raw.Replace("```json", "").Replace("```", "").Trim();

        return JsonSerializer.Deserialize<AiEvolveResponse>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new Exception("Nem sikerült értelmezni az AI JSON válaszát.");
    }

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