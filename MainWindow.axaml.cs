using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Demo;

public partial class MainWindow : Window
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private string _apiKey = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        LoadEnvironment();
    }

    private void LoadEnvironment()
    {
        try {
            DotNetEnv.Env.Load();
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;

            if (string.IsNullOrEmpty(_apiKey)) {
                Console.WriteLine("Figyelem: A GEMINI_API_KEY nem található a .env fájlban!");
            }
        }
        catch (Exception ex) {
            Console.WriteLine($".env betöltési hiba: {ex.Message}");
        }
    }

    private void OnInputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) {
            if (Display.Text == "0" ||
                Display.Text == "Hiba" ||
                Display.Text == "API Hiba" ||
                Display.Text == "AI gondolkodik..." ||
                Display.Text == "Írj be egy feladatot az AI-nak..." ||
                Display.Text == "Hiányzó API kulcs!") {
                Display.Text = "";
            }

            Display.Text += button.Content?.ToString();
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Display.Text = "0";
    }

    private async void OnCalculateClick(object? sender, RoutedEventArgs e)
    {
        var input = Display.Text;
        if (string.IsNullOrWhiteSpace(input)) return;

        try {
            var options = ScriptOptions.Default.WithImports("System", "System.Math");
            var result = await CSharpScript.EvaluateAsync(input, options);
            Display.Text = result?.ToString();
        }
        catch (Exception ex) {
            Display.Text = "Hiba";
            Console.WriteLine($"Roslyn hiba: {ex.Message}");
        }
    }

    private async void OnAiClick(object? sender, RoutedEventArgs e)
    {
        var prompt = Display.Text;

        if (string.IsNullOrWhiteSpace(prompt) || prompt == "0") {
            Display.Text = "Írj be egy feladatot az AI-nak...";
            return;
        }

        if (string.IsNullOrEmpty(_apiKey)) {
            Display.Text = "Hiányzó API kulcs!";
            return;
        }

        Display.Text = "AI gondolkodik...";

        try {
            var generatedCode = await CallGeminiApiAsync(prompt);
            
                generatedCode = generatedCode.Replace("```csharp", "").Replace("```", "").Trim();

            Display.Text = generatedCode;
        }
        catch (Exception ex) {
            Display.Text = "API Hiba";
            Console.WriteLine($"Gemini API hiba: {ex.Message}");
        }
    }

    private async Task<string> CallGeminiApiAsync(string userPrompt)
    {
        string systemInstruction = """
                                   You are a creative and expert C# developer specializing in Roslyn scripting.
                                   Your task is to add a new, interesting, and perhaps unconventional feature to the C# application via dynamic scripting.

                                   Be creative! The new feature does not have to be a simple calculator function. It could be anything that makes sense.

                                   Here are a few examples, but please do not limit yourself to these:
                                   - A complex mathematical calculation, recursive algorithm, or conversion (e.g., Matrix determinant, Base64 encoding).
                                   - A script that returns the current time, date, or system environment information.
                                   - A script that calls a public API (like https://official-joke-api.appspot.com/random_joke or a weather API) and returns the raw JSON.
                                   - A text-based mini-game logic or algorithmic puzzle solver.

                                   Rules:
                                   1. Generate ONLY the C# script code for the new feature.
                                   2. The code must be immediately evaluable by CSharpScript.EvaluateAsync() using the System and System.Math namespaces.
                                   3. Do NOT repeat the existing application code. Only provide the new, self-contained C# script to be executed.
                                   4. Output must be raw C# code only. No markdown formatting (like ```csharp), no explanations, or commentary.
                                   5. The script must evaluate to a result that can be converted to a string and displayed on the calculator's screen.
                                   """;

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_apiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = $"{systemInstruction}\n{userPrompt}".Trim() } } }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        response.EnsureSuccessStatusCode();

        var responseString = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(responseString);
        var root = doc.RootElement;

        var textResult = root.GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        return textResult ?? "Hiba";
    }
}