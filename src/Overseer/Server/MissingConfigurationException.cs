namespace Overseer.Server;

/// <summary>A required setting is missing; the message tells the user how to provide it.</summary>
public sealed class MissingConfigurationException(string message) : Exception(message);
