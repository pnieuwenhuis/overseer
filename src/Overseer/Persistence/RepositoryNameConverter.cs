using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Overseer.GitHub;

namespace Overseer.Persistence;

/// <summary>A repository as PostgreSQL holds it: <c>owner/name</c>, as text.</summary>
public sealed class RepositoryNameConverter()
    : ValueConverter<RepositoryName, string>(repository => repository.ToString(), text => RepositoryName.Parse(text));
