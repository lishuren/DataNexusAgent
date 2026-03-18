namespace DataNexus.Core;

public sealed class GitHubModelsConfig
{
    public string Endpoint { get; set; } = "https://models.inference.ai.azure.com";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
}
