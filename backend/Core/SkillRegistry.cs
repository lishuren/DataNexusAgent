using System.Text;

namespace DataNexus.Core;

public sealed class SkillRegistry(
    IHostEnvironment environment,
    ILogger<SkillRegistry> logger)
{
    private const string SkillManifestFileName = "SKILL.md";
    private readonly string _skillsRoot = Path.GetFullPath(
        Path.Combine(environment.ContentRootPath, "..", ".github", "skills"));
    private readonly string _publicRoot = Path.GetFullPath(
        Path.Combine(environment.ContentRootPath, "..", ".github", "skills", "public"));
    private readonly string _publicBuiltInRoot = Path.GetFullPath(
        Path.Combine(environment.ContentRootPath, "..", ".github", "skills", "public", "builtin"));
    private readonly string _publicUserRoot = Path.GetFullPath(
        Path.Combine(environment.ContentRootPath, "..", ".github", "skills", "public", "user"));
    private readonly string _privateRoot = Path.GetFullPath(
        Path.Combine(environment.ContentRootPath, "..", ".github", "skills", "user"));

    /// <summary>
    /// Ensures the SKILL.md package directories exist and migrates any legacy flat public .md files.
    /// </summary>
    public async Task InitializeAsync(string? seedPath = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_publicBuiltInRoot);
        Directory.CreateDirectory(_publicUserRoot);
        Directory.CreateDirectory(_privateRoot);

        if (seedPath is not null && Directory.Exists(seedPath))
            await MigrateLegacyPublicMarkdownSkillsAsync(seedPath, ct);

        var publicCount = (await GetPublicSkillsAsync(ct)).Count;
        logger.LogInformation(
            "SkillRegistry initialized — {Count} public skills in file-backed storage at {Root}",
            publicCount,
            _skillsRoot);
    }

    /// <summary>
    /// Retrieves all skills available to a user (public + private) from file-backed SKILL.md packages.
    /// </summary>
    public async Task<IReadOnlyList<SkillDefinition>> GetSkillsForUserAsync(
        string userId, CancellationToken ct = default, params string[] skillNames)
    {
        var visibleSkills = await LoadVisibleSkillsAsync(userId, ct);
        if (skillNames.Length == 0)
            return visibleSkills;

        var requestedNames = skillNames
            .Select(NormalizeSkillName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return visibleSkills
            .Where(skill => requestedNames.Contains(NormalizeSkillName(skill.Name)))
            .ToList();
    }

    public async Task<IReadOnlyList<SkillDefinition>> GetPublicSkillsAsync(CancellationToken ct = default)
    {
        return await LoadPublicSkillsAsync(ct);
    }

    public async Task<SkillDefinition?> GetSkillByIdAsync(int id, CancellationToken ct = default)
    {
        var skills = await LoadPublicSkillsAsync(ct);
        return skills.FirstOrDefault(skill => skill.Id == id);
    }

    public async Task<SkillDefinition?> GetSkillByIdForUserAsync(
        string userId, int id, CancellationToken ct = default)
    {
        var skills = await LoadVisibleSkillsAsync(userId, ct);
        return skills.FirstOrDefault(skill => skill.Id == id);
    }

    public async Task<SkillDefinition> CreateUserSkillAsync(
        string userId, string name, string instructions, CancellationToken ct = default)
    {
        var slug = NormalizeSkillName(name);
        await EnsurePrivateSkillNameAvailableAsync(userId, slug, excludeDirectory: null, ct);

        var packageDirectory = GetPrivateSkillDirectory(userId, slug);
        Directory.CreateDirectory(packageDirectory);

        var markdown = ComposeSkillMarkdown(slug, BuildDescription(slug, instructions), instructions);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, SkillManifestFileName), markdown, ct);

        logger.LogInformation("[User: {UserId}] Created file-backed skill '{SkillName}'", userId, slug);
        return await LoadSkillDefinitionAsync(packageDirectory, SkillScope.Private, userId, null, ct);
    }

    public async Task<SkillDefinition> UpdateSkillAsync(
        string userId, int skillId, string name, string instructions, CancellationToken ct = default)
    {
        var existing = await FindOwnedPrivateSkillAsync(userId, skillId, ct)
            ?? throw new ResourceNotFoundException($"Skill {skillId} not found.");

        var slug = NormalizeSkillName(name);
        await EnsurePrivateSkillNameAvailableAsync(userId, slug, existing.PackageDirectory, ct);

        var targetDirectory = GetPrivateSkillDirectory(userId, slug);
        if (!string.Equals(existing.PackageDirectory, targetDirectory, StringComparison.Ordinal))
        {
            if (Directory.Exists(targetDirectory))
                throw new ResourceConflictException($"Skill name '{slug}' already exists.");

            Directory.Move(existing.PackageDirectory!, targetDirectory);
        }

        var markdown = ComposeSkillMarkdown(slug, BuildDescription(slug, instructions), instructions);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, SkillManifestFileName), markdown, ct);

        logger.LogInformation("[User: {UserId}] Updated file-backed skill '{SkillName}'", userId, slug);
        return await LoadSkillDefinitionAsync(targetDirectory, SkillScope.Private, userId, null, ct);
    }

    public async Task DeleteSkillAsync(string userId, int skillId, CancellationToken ct = default)
    {
        var skill = await FindOwnedPrivateSkillAsync(userId, skillId, ct)
            ?? throw new ResourceNotFoundException($"Skill {skillId} not found.");

        Directory.Delete(skill.PackageDirectory!, recursive: true);
        logger.LogInformation("[User: {UserId}] Deleted file-backed skill '{SkillName}'", userId, skill.Name);
    }

    public async Task<SkillDefinition> CloneSkillAsync(
        string userId, int skillId, string newName, CancellationToken ct = default)
    {
        var source = await GetSkillByIdForUserAsync(userId, skillId, ct)
            ?? throw new ResourceNotFoundException($"Skill {skillId} not found.");

        var slug = NormalizeSkillName(newName);
        await EnsurePrivateSkillNameAvailableAsync(userId, slug, excludeDirectory: null, ct);

        var targetDirectory = GetPrivateSkillDirectory(userId, slug);
        await CopyDirectoryAsync(source.PackageDirectory!, targetDirectory, ct);

        var markdown = ComposeSkillMarkdown(slug, source.Description, source.Instructions);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, SkillManifestFileName), markdown, ct);

        logger.LogInformation(
            "[User: {UserId}] Cloned file-backed skill '{SkillName}' as '{CloneName}'",
            userId, source.Name, slug);
        return await LoadSkillDefinitionAsync(targetDirectory, SkillScope.Private, userId, null, ct);
    }

    public async Task<SkillDefinition> PublishSkillAsync(
        string userId, int skillId, CancellationToken ct = default)
    {
        var skill = await FindOwnedPrivateSkillAsync(userId, skillId, ct)
            ?? throw new ResourceNotFoundException($"Skill {skillId} not found.");

        var targetDirectory = GetPublishedSkillDirectory(userId, skill.Name);
        if (Directory.Exists(targetDirectory))
            throw new ResourceConflictException($"Skill name '{skill.Name}' is already published.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
        Directory.Move(skill.PackageDirectory!, targetDirectory);

        logger.LogInformation("[User: {UserId}] Published file-backed skill '{SkillName}' to public", userId, skill.Name);
        return await LoadSkillDefinitionAsync(targetDirectory, SkillScope.Public, null, userId, ct);
    }

    public async Task<SkillDefinition> UnpublishSkillAsync(
        string userId, int skillId, CancellationToken ct = default)
    {
        var skill = await FindPublishedSkillAsync(userId, skillId, ct)
            ?? throw new ResourceNotFoundException($"Skill {skillId} not found.");

        await EnsurePrivateSkillNameAvailableAsync(userId, skill.Name, excludeDirectory: null, ct);

        var targetDirectory = GetPrivateSkillDirectory(userId, skill.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
        Directory.Move(skill.PackageDirectory!, targetDirectory);

        logger.LogInformation("[User: {UserId}] Unpublished file-backed skill '{SkillName}'", userId, skill.Name);
        return await LoadSkillDefinitionAsync(targetDirectory, SkillScope.Private, userId, null, ct);
    }

    private async Task<IReadOnlyList<SkillDefinition>> LoadVisibleSkillsAsync(string userId, CancellationToken ct)
    {
        var publicSkillsTask = LoadPublicSkillsAsync(ct);
        var privateSkillsTask = LoadPrivateSkillsAsync(userId, ct);
        await Task.WhenAll(publicSkillsTask, privateSkillsTask);

        return [..
            publicSkillsTask.Result
                .Concat(privateSkillsTask.Result)
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<IReadOnlyList<SkillDefinition>> LoadPublicSkillsAsync(CancellationToken ct)
    {
        var skillDirectories = EnumerateSkillDirectories(_publicRoot);
        return await LoadSkillDefinitionsAsync(skillDirectories, ct);
    }

    private async Task<IReadOnlyList<SkillDefinition>> LoadPrivateSkillsAsync(string userId, CancellationToken ct)
    {
        var skillDirectories = EnumerateSkillDirectories(GetPrivateUserRoot(userId));
        return await LoadSkillDefinitionsAsync(skillDirectories, ct);
    }

    private async Task<IReadOnlyList<SkillDefinition>> LoadSkillDefinitionsAsync(
        IEnumerable<string> packageDirectories,
        CancellationToken ct)
    {
        var skills = new List<SkillDefinition>();

        foreach (var packageDirectory in packageDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                skills.Add(await LoadSkillDefinitionAsync(packageDirectory, ct));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Skipped invalid skill package at {PackageDirectory}", packageDirectory);
            }
        }

        return skills;
    }

    private async Task<SkillDefinition> LoadSkillDefinitionAsync(
        string packageDirectory,
        CancellationToken ct)
    {
        var (scope, ownerId, publishedByUserId) = ResolveOwnership(packageDirectory);
        return await LoadSkillDefinitionAsync(packageDirectory, scope, ownerId, publishedByUserId, ct);
    }

    private async Task<SkillDefinition> LoadSkillDefinitionAsync(
        string packageDirectory,
        SkillScope scope,
        string? ownerId,
        string? publishedByUserId,
        CancellationToken ct)
    {
        var manifestPath = Path.Combine(packageDirectory, SkillManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Skill manifest not found at {manifestPath}.");

        var markdown = await File.ReadAllTextAsync(manifestPath, ct);
        var parsed = ParseSkillManifest(markdown, Path.GetFileName(packageDirectory));
        var relativePackagePath = Path.GetRelativePath(_skillsRoot, packageDirectory).Replace('\\', '/');

        return new SkillDefinition(
            ComputeStableId(relativePackagePath),
            parsed.Name,
            parsed.Description,
            parsed.Body,
            scope,
            ownerId,
            publishedByUserId,
            packageDirectory);
    }

    private async Task<SkillDefinition?> FindOwnedPrivateSkillAsync(string userId, int skillId, CancellationToken ct)
    {
        var skills = await LoadPrivateSkillsAsync(userId, ct);
        return skills.FirstOrDefault(skill => skill.Id == skillId);
    }

    private async Task<SkillDefinition?> FindPublishedSkillAsync(string userId, int skillId, CancellationToken ct)
    {
        var skills = await LoadPublicSkillsAsync(ct);
        return skills.FirstOrDefault(skill =>
            skill.Id == skillId &&
            string.Equals(skill.PublishedByUserId, userId, StringComparison.Ordinal));
    }

    private async Task EnsurePrivateSkillNameAvailableAsync(
        string userId,
        string slug,
        string? excludeDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var targetDirectory = GetPrivateSkillDirectory(userId, slug);
        if (excludeDirectory is not null && string.Equals(targetDirectory, excludeDirectory, StringComparison.Ordinal))
            return;

        if (Directory.Exists(targetDirectory))
            throw new ResourceConflictException($"Skill name '{slug}' already exists.");
    }

    private async Task MigrateLegacyPublicMarkdownSkillsAsync(string seedPath, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(seedPath, "*.md", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            if (string.Equals(Path.GetFileName(file), SkillManifestFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var slug = NormalizeSkillName(Path.GetFileNameWithoutExtension(file));
            var packageDirectory = GetBuiltInSkillDirectory(slug);
            var manifestPath = Path.Combine(packageDirectory, SkillManifestFileName);

            if (File.Exists(manifestPath))
                continue;

            Directory.CreateDirectory(packageDirectory);
            var content = await File.ReadAllTextAsync(file, ct);
            var description = BuildDescription(slug, content);
            await File.WriteAllTextAsync(manifestPath, ComposeSkillMarkdown(slug, description, content), ct);

            logger.LogInformation("Migrated legacy public skill '{SkillName}' to {PackageDirectory}", slug, packageDirectory);
        }
    }

    private static IEnumerable<string> EnumerateSkillDirectories(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            yield break;

        foreach (var manifestPath in Directory.EnumerateFiles(rootDirectory, SkillManifestFileName, SearchOption.AllDirectories))
            yield return Path.GetDirectoryName(manifestPath)!;
    }

    private (SkillScope Scope, string? OwnerId, string? PublishedByUserId) ResolveOwnership(string packageDirectory)
    {
        var relativePath = Path.GetRelativePath(_skillsRoot, packageDirectory).Replace('\\', '/');
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 3 && string.Equals(parts[0], "user", StringComparison.OrdinalIgnoreCase))
            return (SkillScope.Private, DecodePathSegment(parts[1]), null);

        if (parts.Length >= 4 &&
            string.Equals(parts[0], "public", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[1], "user", StringComparison.OrdinalIgnoreCase))
        {
            return (SkillScope.Public, null, DecodePathSegment(parts[2]));
        }

        return (SkillScope.Public, null, null);
    }

    private string GetPrivateUserRoot(string userId) =>
        Path.Combine(_privateRoot, EncodePathSegment(userId));

    private string GetPrivateSkillDirectory(string userId, string slug) =>
        Path.Combine(GetPrivateUserRoot(userId), slug);

    private string GetPublishedSkillDirectory(string userId, string slug) =>
        Path.Combine(_publicUserRoot, EncodePathSegment(userId), slug);

    private string GetBuiltInSkillDirectory(string slug) =>
        Path.Combine(_publicBuiltInRoot, slug);

    private static ParsedSkillManifest ParseSkillManifest(string markdown, string fallbackName)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        var name = NormalizeSkillName(fallbackName);
        var description = string.Empty;
        var body = normalized.Trim();

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var closingIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (closingIndex > 0)
            {
                var frontmatter = normalized[4..closingIndex];
                body = normalized[(closingIndex + 5)..].TrimStart('\n');

                foreach (var line in frontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex <= 0)
                        continue;

                    var key = line[..separatorIndex].Trim();
                    var value = line[(separatorIndex + 1)..].Trim().Trim('"', '\'');

                    if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                        name = NormalizeSkillName(value);
                    else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                        description = value;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(description))
            description = BuildDescription(name, body);

        return new ParsedSkillManifest(name, description, body.Trim());
    }

    private static string ComposeSkillMarkdown(string name, string description, string body)
    {
        var normalizedBody = body.Replace("\r\n", "\n").Trim();
        return $"---\nname: {name}\ndescription: {EscapeYaml(description)}\n---\n\n{normalizedBody}\n";
    }

    private static string BuildDescription(string name, string instructions)
    {
        foreach (var line in instructions.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith('#') || trimmed.StartsWith('-') || trimmed.StartsWith('*') || trimmed.StartsWith('|') || trimmed.StartsWith("```") || trimmed.StartsWith('>'))
                continue;

            return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "...";
        }

        return $"Skill package for {name}";
    }

    private static string EscapeYaml(string value) => value.Replace(":", " -");

    public static string NormalizeSkillName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            throw new InvalidOperationException("Skill name is required.");

        var builder = new StringBuilder();
        var previousWasSeparator = false;
        var previousWasLowerOrDigit = false;

        foreach (var character in rawName.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (char.IsUpper(character) && previousWasLowerOrDigit && builder.Length > 0 && !previousWasSeparator)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                previousWasLowerOrDigit = char.IsLower(character) || char.IsDigit(character);
                continue;
            }

            if (builder.Length > 0 && !previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }

            previousWasLowerOrDigit = false;
        }

        var normalized = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Skill name must contain letters or numbers.");

        return normalized;
    }

    private static string EncodePathSegment(string value) => Uri.EscapeDataString(value);

    private static string DecodePathSegment(string value) => Uri.UnescapeDataString(value);

    private static int ComputeStableId(string key)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in key)
            {
                hash ^= character;
                hash *= 16777619;
            }

            return (int)(hash & 0x7fffffff);
        }
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string targetDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            await using var sourceStream = File.OpenRead(file);
            await using var destinationStream = File.Create(destinationFile);
            await sourceStream.CopyToAsync(destinationStream, ct);
        }
    }

    private sealed record ParsedSkillManifest(string Name, string Description, string Body);
}
