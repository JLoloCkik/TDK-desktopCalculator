using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Google.GenAI;

namespace Demo;

// -----------------------------------------------------------------------
// Globals osztály: ezt kapja meg a Roslyn szkript "kontextusként".
// A szkript ezen keresztül fér hozzá az élő ablakhoz.
// -----------------------------------------------------------------------
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
    // -----------------------------------------------------------------------
    // Megosztott Roslyn ScriptState – ez az alkalmazás "memóriája".
    // Minden egymást követő szkript ebben az állapotban fut, így
    // a korábban definiált változók és metódusok megmaradnak.
    // -----------------------------------------------------------------------
    private ScriptState<object>? _scriptState;
    private readonly ScriptGlobals _globals;

    // Az AI által létrehozott gombok nyilvántartása (név → kód)
    private readonly List<(string Label, string Code)> _dynamicFeatures = new();

    public MainWindow()
    {
        InitializeComponent();
        _globals = new ScriptGlobals { Window = this };
        LoadEnvironment();
    }

    // -----------------------------------------------------------------------
    // .env betöltés
    // -----------------------------------------------------------------------
    private void LoadEnvironment()
    {
        try
        {
            DotNetEnv.Env.Load();
            var myApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrEmpty(myApiKey))
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", myApiKey);

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")))
                SetStatus("⚠️ GOOGLE_API_KEY hiányzik a .env fájlból!", isError: true);
            else
                SetStatus("✅ API kulcs betöltve. Írj be egy kérést és nyomj 🤖 AI Funkció gombot!");
        }
        catch (Exception ex)
        {
            SetStatus($".env hiba: {ex.Message}", isError: true);
        }
    }

    // -----------------------------------------------------------------------
    // Statikus gomb kezelők (szám/operátor bevitel)
    // -----------------------------------------------------------------------
    private void OnInputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var clearStates = new[] { "0", "Hiba", "API Hiba", "AI gondolkodik...",
            "Írj be egy kérést...", "Hiányzó API kulcs!" };

        if (clearStates.Contains(Display.Text))
            Display.Text = "";

        Display.Text += button.Content?.ToString();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Display.Text = "0";
        SetStatus("Törölve.");
    }

    // -----------------------------------------------------------------------
    // = gomb: Roslyn alapú kifejezés kiértékelés (kalkulátor mód)
    // -----------------------------------------------------------------------
    private async void OnCalculateClick(object? sender, RoutedEventArgs e)
    {
        var input = Display.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        try
        {
            var options = ScriptOptions.Default
                .WithImports("System", "System.Math");
            var result = await CSharpScript.EvaluateAsync(input, options);
            Display.Text = result?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            Display.Text = "Hiba";
            SetStatus($"Kiértékelési hiba: {ex.Message}", isError: true);
        }
    }

    // -----------------------------------------------------------------------
    // 🤖 AI GOMB – Ez a self-evolving mag
    // -----------------------------------------------------------------------
    private async void OnAiClick(object? sender, RoutedEventArgs e)
    {
        var prompt = Display.Text?.Trim();

        if (string.IsNullOrWhiteSpace(prompt) || prompt == "0")
        {
            SetStatus("Írj be egy kérést a kijelzőre, majd nyomj AI Funkció gombot!", isError: true);
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")))
        {
            SetStatus("Hiányzó API kulcs! Ellenőrizd a .env fájlt.", isError: true);
            return;
        }

        // UI lezárás a generálás idejére
        AiButton.IsEnabled = false;
        SetStatus("🤖 AI generálja a kódot...");

        try
        {
            // 1. Gemini API hívás → nyers C# kód
            var generatedCode = await CallGeminiApiAsync(prompt);

            // 2. Kód tisztítás (markdown blokkok eltávolítása)
            generatedCode = CleanGeneratedCode(generatedCode);

            // 3. Dinamikus funkció hozzáadása az UI-hoz
            await AddDynamicFeatureAsync(prompt, generatedCode);
        }
        catch (Exception ex)
        {
            SetStatus($"AI hiba: {ex.Message}", isError: true);
        }
        finally
        {
            AiButton.IsEnabled = true;
        }
    }

    // -----------------------------------------------------------------------
    // Gemini API hívás – CSAK KÓDOT kérünk, UI-t módosító szkriptet
    // -----------------------------------------------------------------------
    private async Task<string> CallGeminiApiAsync(string userPrompt)
    {
        // A system prompt most DINAMIKUS AVALONIA UI-t kér, nem egyszerű kifejezést
        string systemInstruction = """
            You are a senior C# developer. Your task is to generate a C# script
            that will be compiled and executed using Roslyn ScriptState with Globals.

            The script has access to a `Window` variable (type: Avalonia MainWindow)
            and a `DisplayText` property (string, get/set) via the globals object.

            RULES:
            1. The script MUST set `DisplayText` to show a result to the user.
            2. You can use `Window` to access Avalonia UI elements if needed.
            3. The script runs in the context of the globals object - access `DisplayText` directly.
            4. Available namespaces: System, System.Math, System.Linq, System.Collections.Generic,
               System.Net.Http, System.Threading.Tasks, Avalonia.Controls, Avalonia.Media.
            5. For async operations, use async/await. The script can be async.
            6. For HTTP calls, use `new System.Net.Http.HttpClient()`.
            7. Output ONLY raw C# code. NO markdown, NO explanation, NO ```csharp fences.
            8. The script should be self-contained and meaningful.
            9. Be CREATIVE: time/date queries, math algorithms, API calls, system info, etc.
            10. If you define methods or classes, keep them inside the script scope.

            EXAMPLE for a simple task "show current time":
            DisplayText = $"Most: {DateTime.Now:HH:mm:ss}";

            EXAMPLE for a math task "fibonacci 10":
            int Fib(int n) => n <= 1 ? n : Fib(n-1) + Fib(n-2);
            DisplayText = $"Fibonacci(10) = {Fib(10)}";

            EXAMPLE for an async API call "random joke":
            using var http = new System.Net.Http.HttpClient();
            var json = await http.GetStringAsync("https://official-joke-api.appspot.com/random_joke");
            DisplayText = json;
            """;

        var client = new Client();
        var fullPrompt = $"{systemInstruction}\n\nFELADAT: {userPrompt}";

        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: fullPrompt
        );

        return response.Candidates[0].Content.Parts[0].Text ?? throw new Exception("Üres AI válasz");
    }

    // -----------------------------------------------------------------------
    // Generált kód tisztítása
    // -----------------------------------------------------------------------
    private static string CleanGeneratedCode(string code)
    {
        return code
            .Replace("```csharp", "")
            .Replace("```cs", "")
            .Replace("```", "")
            .Trim();
    }

    // -----------------------------------------------------------------------
    // DINAMIKUS BŐVÍTÉS MAGJA
    // A generált kód Roslyn ScriptState-be kerül,
    // és egy új gomb jelenik meg a DynamicButtonPanel-en.
    // -----------------------------------------------------------------------
    private async Task AddDynamicFeatureAsync(string label, string code)
    {
        // Egyedi, rövid gombnév generálása a promptból
        var buttonLabel = GenerateButtonLabel(label);

        SetStatus($"⚙️ Kód fordítása: {buttonLabel}...");

        // Roslyn ScriptOptions összeállítása – minden szükséges referencia
        var options = ScriptOptions.Default
            .WithReferences(
                typeof(object).Assembly,                          // mscorlib / System.Private.CoreLib
                typeof(Enumerable).Assembly,                      // System.Linq
                typeof(System.Net.Http.HttpClient).Assembly,      // System.Net.Http
                typeof(Window).Assembly,                          // Avalonia.Controls
                typeof(Avalonia.Media.Color).Assembly,            // Avalonia.Media (Color, SolidColorBrush)
                Assembly.GetExecutingAssembly()                   // Demo (MainWindow stb.)
            )
            .WithImports(
                "System",
                "System.Math",
                "System.Linq",
                "System.Collections.Generic",
                "System.Net.Http",
                "System.Threading.Tasks",
                "Avalonia.Controls",
                "Avalonia.Media"
            );

        try
        {
            // Futtatás ScriptState-ben Globals-szal
            if (_scriptState == null)
            {
                _scriptState = await CSharpScript.RunAsync(
                    code,
                    options,
                    globals: _globals,
                    globalsType: typeof(ScriptGlobals)
                );
            }
            else
            {
                _scriptState = await _scriptState.ContinueWithAsync(code);
            }

            // Sikeres futás → gomb hozzáadása az UI-hoz (UI szálon!)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AddDynamicButton(buttonLabel, code);
            });

            _dynamicFeatures.Add((buttonLabel, code));
            SetStatus($"✅ '{buttonLabel}' sikeresen hozzáadva! Összes AI funkció: {_dynamicFeatures.Count}");
        }
        catch (CompilationErrorException ex)
        {
            Display.Text = "Fordítási hiba";
            var errors = string.Join(", ", ex.Diagnostics.Select(d => d.GetMessage()));
            SetStatus($"❌ Roslyn fordítási hiba: {errors}", isError: true);
        }
        catch (Exception ex)
        {
            Display.Text = "Futási hiba";
            SetStatus($"❌ Futási hiba: {ex.Message}", isError: true);
        }
    }

    // -----------------------------------------------------------------------
    // Új gomb hozzáadása a DynamicButtonPanel-hez (UI szálon kell hívni!)
    // -----------------------------------------------------------------------
    private void AddDynamicButton(string label, string code)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 13,
            Padding = new Thickness(10, 6),
            Margin = new Thickness(3),
            Background = new SolidColorBrush(Color.Parse("#2979FF")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            Tag = code  // A kód referenciája a gombhoz mentve
        };

        // Kattintáskor újrafuttatja a tárolt kódot
        button.Click += async (sender, _) =>
        {
            if (sender is not Button btn) return;
            var storedCode = btn.Tag as string ?? "";

            btn.IsEnabled = false;
            SetStatus($"⚡ '{label}' futtatása...");

            try
            {
                if (_scriptState == null)
                {
                    var opts = ScriptOptions.Default
                        .WithReferences(
                            typeof(object).Assembly,
                            typeof(Enumerable).Assembly,
                            typeof(System.Net.Http.HttpClient).Assembly,
                            typeof(Window).Assembly,
                            typeof(Avalonia.Media.Color).Assembly,
                            Assembly.GetExecutingAssembly()
                        )
                        .WithImports("System", "System.Math", "System.Linq",
                            "System.Collections.Generic", "System.Net.Http",
                            "System.Threading.Tasks", "Avalonia.Controls", "Avalonia.Media");

                    _scriptState = await CSharpScript.RunAsync(
                        storedCode, opts, globals: _globals, globalsType: typeof(ScriptGlobals));
                }
                else
                {
                    _scriptState = await _scriptState.ContinueWithAsync(storedCode);
                }

                SetStatus($"✅ '{label}' sikeresen futott.");
            }
            catch (Exception ex)
            {
                Display.Text = "Hiba";
                SetStatus($"❌ '{label}' hiba: {ex.Message}", isError: true);
            }
            finally
            {
                btn.IsEnabled = true;
            }
        };

        // Tooltip: megmutatja az eredeti promptot / kódot
        ToolTip.SetTip(button, $"Kód:\n{code[..Math.Min(code.Length, 200)]}...");

        DynamicButtonPanel.Children.Add(button);
    }

    // -----------------------------------------------------------------------
    // Rövid gombnév generálása a felhasználó promptjából
    // -----------------------------------------------------------------------
    private static string GenerateButtonLabel(string prompt)
    {
        // Max 20 karakter, szép formában
        var clean = prompt.Trim();
        return clean.Length <= 20
            ? clean
            : clean[..17].TrimEnd() + "...";
    }

    // -----------------------------------------------------------------------
    // Státuszsor frissítése
    // -----------------------------------------------------------------------
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
}