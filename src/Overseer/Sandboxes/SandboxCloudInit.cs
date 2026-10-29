namespace Overseer.Sandboxes;

/// <summary>
/// The cloud-init configuration every Azure Sandbox boots with: the SSH host key Overseer generated, so it knows the
/// key before it first connects, and the setup script that installs the Sandbox's tools.
/// </summary>
internal static class SandboxCloudInit
{
    public static string For(SshKeyPair hostKey) => $"""
        #cloud-config
        # Only Overseer's host key: no keys of the VM's own making, which Overseer could not tell from an impostor's.
        # cloud-init generates only the listed types it has no key for, so listing just rsa generates none.
        ssh_deletekeys: true
        ssh_genkeytypes: [rsa]
        ssh_keys:
          rsa_private: |
        {Indented(hostKey.PrivateKey, "    ")}
          rsa_public: {hostKey.PublicKey}
        write_files:
          - path: /opt/overseer/setup-sandbox.sh
            permissions: "0755"
            encoding: b64
            content: {Convert.ToBase64String(SetupScript())}
        runcmd:
          - [/opt/overseer/setup-sandbox.sh]

        """;

    private static string Indented(string text, string indentation) =>
        string.Join('\n', text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line => indentation + line));

    private static byte[] SetupScript()
    {
        using var stream = typeof(SandboxCloudInit).Assembly.GetManifestResourceStream("Overseer.Sandboxes.Setup.setup-sandbox.sh")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
