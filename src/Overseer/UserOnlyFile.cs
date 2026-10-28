namespace Overseer;

/// <summary>Files that only the user may read, for configuration and secrets.</summary>
public static class UserOnlyFile
{
    /// <summary>Creates <paramref name="path"/>, which must not exist yet, for writing.</summary>
    public static FileStream CreateNew(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }
}
