using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public sealed class VerifyNativeArtifact : Task
{
    [Required] public string PinFile { get; set; }
    [Required] public string ArtifactDirectory { get; set; }
    [Required] public string LibraryFile { get; set; }
    [Required] public string RebuildCommand { get; set; }

    [DataContract] public sealed class Dependency
    {
        [DataMember] public string version;
        [DataMember] public string sha256;
    }
    [DataContract] public sealed class Pins
    {
        [DataMember] public Dependency ghostty;
        [DataMember] public Dependency zig;
    }
    [DataContract] public sealed class Provenance
    {
        [DataMember] public string ghostty;
        [DataMember] public string sourceSha256;
        [DataMember] public string zig;
        [DataMember] public string dllSha256;
    }
    private static T Read<T>(string path)
    {
        using (var stream = File.OpenRead(path))
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
    }
    public override bool Execute()
    {
        try
        {
            var pin = Read<Pins>(PinFile);
            var manifest = Read<Provenance>(Path.Combine(ArtifactDirectory, "native-build.json"));
            if (pin.ghostty == null || pin.zig == null || String.IsNullOrEmpty(pin.ghostty.version) ||
                String.IsNullOrEmpty(pin.ghostty.sha256) || String.IsNullOrEmpty(pin.zig.version))
                throw new InvalidDataException("Invalid native dependency pin.");
            if (manifest.ghostty != pin.ghostty.version || manifest.zig != pin.zig.version ||
                !String.Equals(manifest.sourceSha256, pin.ghostty.sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native provenance does not match native-dependencies.json.");
            string actual;
            using (var stream = File.OpenRead(LibraryFile))
            using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(actual, manifest.dllSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native library SHA256 does not match native-build.json.");
            return true;
        }
        catch (Exception exception)
        {
            Log.LogError("Native artifact verification failed for '{0}': {1} Rebuild from the repository root with: {2}",
                LibraryFile, exception.Message, RebuildCommand);
            return false;
        }
    }
}
