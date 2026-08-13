using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

const string PromptFile = "prompt.md";
const string PdfArchiveDirectory = "pdf-archived";

string? apiKey = Environment.GetEnvironmentVariable("openrouter");

if (string.IsNullOrWhiteSpace(apiKey))
{
  Console.Error.WriteLine("Environment variable 'openrouter' is not set.");
  return 1;
}

// Usage:
//   openrouter <model> -r=<reasoning-effort> -v=<verbosity> [pdf]
//
// Example:
//   openrouter openai/5.6-sol -r=none -v=low pdf

if (args.Length is < 3 or > 4)
{
  Console.Error.WriteLine(
      "Usage: openrouter <model> -r=<reasoning-effort> -v=<verbosity> [pdf]");
  Console.Error.WriteLine(
      "Example: openrouter openai/5.6-sol -r=none -v=low pdf");
  return 1;
}

string model = args[0];

if (!args[1].StartsWith("-r=", StringComparison.OrdinalIgnoreCase))
{
  Console.Error.WriteLine("Expected reasoning effort in the form -r=<value>.");
  return 1;
}

if (!args[2].StartsWith("-v=", StringComparison.OrdinalIgnoreCase))
{
  Console.Error.WriteLine("Expected verbosity in the form -v=<value>.");
  return 1;
}

string reasoningEffort = args[1][3..].ToLowerInvariant();
string verbosity = args[2][3..].ToLowerInvariant();
bool sendPdfs = args.Length == 4 &&
    args[3].Equals("pdf", StringComparison.OrdinalIgnoreCase);

if (args.Length == 4 && !sendPdfs)
{
  Console.Error.WriteLine("Expected optional command 'pdf'.");
  return 1;
}

// Model-specific validation.
// Add additional model-specific rules here as needed.
if (model.Equals("openai/5.6-sol", StringComparison.OrdinalIgnoreCase) ||
    model.Equals("openai/gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))
{
  string[] validReasoningEfforts =
  {
    "none",
    "minimal",
    "low",
    "medium",
    "high",
    "xhigh"
  };

  string[] validVerbosity =
  {
    "low",
    "medium",
    "high"
  };

  if (!validReasoningEfforts.Contains(reasoningEffort))
  {
    Console.Error.WriteLine(
        $"Invalid reasoning effort '{reasoningEffort}' for model '{model}'.");
    Console.Error.WriteLine(
        "Valid values: none, minimal, low, medium, high, xhigh");
    return 1;
  }

  if (!validVerbosity.Contains(verbosity))
  {
    Console.Error.WriteLine(
        $"Invalid verbosity '{verbosity}' for model '{model}'.");
    Console.Error.WriteLine(
        "Valid values: low, medium, high");
    return 1;
  }
}
else
{
  Console.Error.WriteLine(
      $"No parameter configuration is defined for model '{model}'.");
  Console.Error.WriteLine(
      "Add the model and its supported parameters to the model-specific validation section.");
  return 1;
}

if (!File.Exists(PromptFile))
{
  Console.Error.WriteLine(
      $"File '{PromptFile}' was not found in the current directory.");
  return 1;
}

string prompt = await File.ReadAllTextAsync(PromptFile);
string[] pdfFiles = sendPdfs
    ? Directory.GetFiles(
        Directory.GetCurrentDirectory(),
        "*.pdf",
        SearchOption.TopDirectoryOnly)
    : [];

if (string.IsNullOrWhiteSpace(prompt))
{
  Console.Error.WriteLine($"File '{PromptFile}' is empty.");
  return 1;
}

using var http = new HttpClient
{
  BaseAddress = new Uri("https://openrouter.ai")
};

http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", apiKey);

var messageContent = new List<object>
{
  new
  {
    type = "text",
    text = prompt
  }
};

foreach (string pdfFile in pdfFiles)
{
  byte[] pdfBytes = await File.ReadAllBytesAsync(pdfFile);
  messageContent.Add(new
  {
    type = "file",
    file = new
    {
      filename = Path.GetFileName(pdfFile),
      file_data = $"data:application/pdf;base64,{Convert.ToBase64String(pdfBytes)}"
    }
  });
}

var request = new
{
  model = model,
  messages = new[]
  {
    new
    {
      role = "user",
      content = messageContent
    }
  },

  // OpenRouter normalized reasoning parameter.
  reasoning = new
  {
    effort = reasoningEffort
  },

  // OpenAI-compatible verbosity parameter.
  verbosity = verbosity
};

string json = JsonSerializer.Serialize(request);

using var content = new StringContent(
    json,
    Encoding.UTF8,
    "application/json");

using HttpResponseMessage response =
    await http.PostAsync("/api/v1/chat/completions", content);

string responseBody = await response.Content.ReadAsStringAsync();

if (!response.IsSuccessStatusCode)
{
  Console.Error.WriteLine(
      $"OpenRouter returned {(int)response.StatusCode} {response.StatusCode}:");
  Console.Error.WriteLine(responseBody);
  return 1;
}

using JsonDocument document = JsonDocument.Parse(responseBody);
JsonElement root = document.RootElement;

string answer = root
    .GetProperty("choices")[0]
    .GetProperty("message")
    .GetProperty("content")
    .GetString() ?? string.Empty;

JsonElement usage = root.GetProperty("usage");

long inputTokens =
    usage.TryGetProperty("prompt_tokens", out var promptTokens)
        ? promptTokens.GetInt64()
        : 0;

long cachedTokens = 0;

if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) &&
    promptDetails.TryGetProperty("cached_tokens", out var cached))
{
  cachedTokens = cached.GetInt64();
}

long outputTokens =
    usage.TryGetProperty("completion_tokens", out var completionTokens)
        ? completionTokens.GetInt64()
        : 0;

long totalTokens =
    usage.TryGetProperty("total_tokens", out var total)
        ? total.GetInt64()
        : inputTokens + outputTokens;

long reasoningTokens = 0;

if (usage.TryGetProperty(
        "completion_tokens_details",
        out var completionDetails) &&
    completionDetails.TryGetProperty(
        "reasoning_tokens",
        out var reasoning))
{
  reasoningTokens = reasoning.GetInt64();
}

string metadata =
    $"m={model}, i={inputTokens}, c={cachedTokens}, o={outputTokens}, " +
    $"r={reasoningTokens}, t={totalTokens}";

const string separator = "---";

// Ensure the first separator always starts on its own line.
string addition =
    $"{Environment.NewLine}" +
    $"{separator}{Environment.NewLine}" +
    $"{answer.TrimEnd()}{Environment.NewLine}" +
    $"{separator}{Environment.NewLine}" +
    $"{metadata}{Environment.NewLine}";

await File.AppendAllTextAsync(PromptFile, addition);

if (pdfFiles.Length > 0)
{
  Directory.CreateDirectory(PdfArchiveDirectory);

  foreach (string pdfFile in pdfFiles)
  {
    string destination = Path.Combine(
        PdfArchiveDirectory,
        Path.GetFileName(pdfFile));
    File.Move(pdfFile, destination, overwrite: true);
  }
}

Console.WriteLine(answer);

return 0;