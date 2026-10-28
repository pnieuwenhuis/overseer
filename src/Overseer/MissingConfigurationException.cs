namespace Overseer;

/// <summary>A required setting is missing or unusable; the message tells the user how to provide it.</summary>
public sealed class MissingConfigurationException(string message) : Exception(message);
