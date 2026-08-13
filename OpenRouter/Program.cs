using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

const string Model = "openai/5.6-sol";
const string PromptFile = "prompt.md";

string? apiKey = Environment.GetEnvironmentVariable("openrouter");

if (string.IsNullOrWhiteSpace(apiKey))
{
  Console.Error.WriteLine("Environment variable 'openrouter' is not set.");
  return 1;
}

if (!File.Exists(PromptFile))
{
  Console.Error.WriteLine($"File '{PromptFile}' was not found in the current directory.");
  return 1;
}

string prompt = await File.ReadAllTextAsync(PromptFile);

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

var request = new
{
  model = Model,
  messages = new[]
    {
        new
        {
            role = "user",
            content = prompt
        }
    },
  reasoning = new
  {
    effort = "none"
  }
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

long inputTokens = usage.TryGetProperty("prompt_tokens", out var promptTokens)
    ? promptTokens.GetInt64()
    : 0;

long cachedTokens = 0;

if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) &&
    promptDetails.TryGetProperty("cached_tokens", out var cached))
{
  cachedTokens = cached.GetInt64();
}

long outputTokens = usage.TryGetProperty("completion_tokens", out var completionTokens)
    ? completionTokens.GetInt64()
    : 0;

string metadata =
    $"model={Model}, input-tokens={inputTokens}, cached={cachedTokens}, output={outputTokens}";

string separator = "---";

string addition =
    $"{separator}{Environment.NewLine}" +
    $"{answer.TrimEnd()}{Environment.NewLine}" +
    $"{separator}{Environment.NewLine}" +
    $"{metadata}{Environment.NewLine}";

await File.AppendAllTextAsync(PromptFile, addition);

Console.WriteLine(answer);

return 0;