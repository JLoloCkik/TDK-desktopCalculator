using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Google.GenAI;

namespace Demo;

public partial class MainWindow : Window
{
    private readonly string _axamlPath = "MainWindow.axaml";
    private readonly string _csPath = "MainWindow.axaml.cs";

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
            var myApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrEmpty(myApiKey))
            {
                Environment.SetEnvironmentVariable("GOOGLE_API_KEY", myApiKey);
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
            if (Display.Text == "0" || Display.Text!.Contains("Hiba") || Display.Text!.Contains("AI"))
                Display.Text = "";

            Display.Text += button.Content?.ToString();
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Display.Text = "0";
    }

    private void OnCalculateClick(object? sender, RoutedEventArgs e)
    {
        Display.Text = "Kérlek használd az AI gombot a fejlesztéshez!";
    }

    private async void OnAiClick(object? sender, RoutedEventArgs e)
    {
        var prompt = Display.Text;

        if (string.IsNullOrWhiteSpace(prompt) || prompt == "0")
        {
            Display.Text = "Írd be, mit fejlesszen az AI...";
            return;
        }

        Display.Text = "AI kódol és fájlokat módosít...";

        try
        {
            await EvolveApplicationAsync(prompt);
            Display.Text = "Kész! Zárd be, és indítsd újra (dotnet run)";
        }
        catch (Exception ex)
        {
            Display.Text = "Hiba történt a generáláskor";
            Console.WriteLine($"Self-Evolving Hiba: {ex.Message}");
        }
    }

    private async Task EvolveApplicationAsync(string userPrompt)
    {
        string currentAxaml = await File.ReadAllTextAsync(_axamlPath);
        string currentCs = await File.ReadAllTextAsync(_csPath);

        string systemInstruction = """
        You are an expert Avalonia and C# developer. You are part of a self-evolving application.
        The user wants to add a new feature, a new UI element, or change the logic.
        
        You will receive the CURRENT 'MainWindow.axaml' and 'MainWindow.axaml.cs' code.
        You must return the FULLY UPDATED content of BOTH files. Do not truncate the code.
        
        CRITICAL RULES:
        1. Always maintain the Avalonia boilerplate and existing functionality unless asked to remove it.
        2. Ensure the XAML elements have proper names (x:Name) if you want to access them from C#.
        3. You MUST output EXACTLY in this format, and nothing else (no markdown around the whole response):
        
        ///AXAML_START///
        [Your full updated XAML code here]
        ///AXAML_END///
        ///CS_START///
        [Your full updated C# code here]
        ///CS_END///
        """;

        var client = new Client();
        var fullPrompt = $"{systemInstruction}\n\nUSER REQUEST:\n{userPrompt}\n\nCURRENT AXAML:\n{currentAxaml}\n\nCURRENT CS:\n{currentCs}";

        var response = await client.Models.GenerateContentAsync(
            model: "gemini-2.5-flash",
            contents: fullPrompt
        );

        string responseText = response.Candidates[0].Content.Parts[0].Text ?? "";
        
        ExtractAndWriteCode(responseText);
    }

    private void ExtractAndWriteCode(string aiResponse)
    {
        try
        {
            int axamlStart = aiResponse.IndexOf("///AXAML_START///") + "///AXAML_START///".Length;
            int axamlEnd = aiResponse.IndexOf("///AXAML_END///");
            int csStart = aiResponse.IndexOf("///CS_START///") + "///CS_START///".Length;
            int csEnd = aiResponse.IndexOf("///CS_END///");

            if (axamlStart > -1 && axamlEnd > -1 && csStart > -1 && csEnd > -1)
            {
                string newAxaml = aiResponse.Substring(axamlStart, axamlEnd - axamlStart).Trim();
                string newCs = aiResponse.Substring(csStart, csEnd - csStart).Trim();
                
                File.WriteAllText(_axamlPath, newAxaml);
                File.WriteAllText(_csPath, newCs);
                Console.WriteLine("A fájlok sikeresen frissítve lettek!");
            }
            else
            {
                throw new Exception("Az AI nem a megfelelő formátumban válaszolt.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fájlírási hiba: {ex.Message}");
            throw;
        }
    }
}