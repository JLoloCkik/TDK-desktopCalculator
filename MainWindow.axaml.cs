using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Google.GenAI;

namespace Demo;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LoadEnvironment();
    }

    private void LoadEnvironment()
    {
        try
        {
            DotNetEnv.Env.Load();
            
            // 1. Kiolvassuk a mi eddigi kulcsunkat
            var myApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            
            // 2. Ha megvan, beállítjuk a hivatalos SDK által elvárt néven is!
            if (!string.IsNullOrEmpty(myApiKey))
            {
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", myApiKey);
            }
            
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")))
            {
                Console.WriteLine("Figyelem: Az API kulcs nem található a .env fájlban!");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($".env betöltési hiba: {ex.Message}");
        }
    }

    private void OnInputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            if (Display.Text == "0" ||
                Display.Text == "Hiba" ||
                Display.Text == "API Hiba" ||
                Display.Text == "AI gondolkodik..." ||
                Display.Text == "Írj be egy feladatot az AI-nak..." ||
                Display.Text == "Hiányzó API kulcs!")
            {
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

        try
        {
            var options = ScriptOptions.Default.WithImports("System", "System.Math");
            var result = await CSharpScript.EvaluateAsync(input, options);
            Display.Text = result?.ToString();
        }
        catch (Exception ex)
        {
            Display.Text = "Hiba";
            Console.WriteLine($"Roslyn hiba: {ex.Message}");
        }
    }

    private async void OnAiClick(object? sender, RoutedEventArgs e)
    {
        var prompt = Display.Text;

        if (string.IsNullOrWhiteSpace(prompt) || prompt == "0")
        {
            Display.Text = "Írj be egy feladatot az AI-nak...";
            return;
        }

        // Itt már az új nevet ellenőrizzük
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")))
        {
            Display.Text = "Hiányzó API kulcs!";
            return;
        }

        Display.Text = "AI gondolkodik...";

        try
        {
            var generatedCode = await CallGeminiApiAsync(prompt);
            
            generatedCode = generatedCode.Replace("```csharp", "").Replace("```", "").Trim();

            Display.Text = generatedCode;
        }
        catch (Exception ex)
        {
            Display.Text = "API Hiba";
            Console.WriteLine($"Gemini API SDK hiba: {ex.Message}");
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

        // A kliens most már automatikusan megtalálja a GOOGLE_API_KEY-t
        var client = new Client();

        var fullPrompt = $"{systemInstruction}\n\nFeladat: {userPrompt}";

        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: fullPrompt
        );

        return response.Candidates[0].Content.Parts[0].Text ?? "Hiba";
    }
}