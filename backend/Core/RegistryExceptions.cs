namespace DataNexus.Core;

public sealed class ResourceNotFoundException(string message) : Exception(message);

public sealed class ResourceConflictException(string message) : Exception(message);

public sealed class ResourceAccessDeniedException(string message) : Exception(message);