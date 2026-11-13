using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AgentTerm.Copilot;

/// <summary>Checks a final response against the streamed UTF-16 prefix without retaining response text.</summary>
internal sealed class ResponseReconciliation : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    internal int Length { get; private set; }

    internal void Append(string text)
    {
        _hash.AppendData(MemoryMarshal.AsBytes(text.AsSpan()));
        Length = checked(Length + text.Length);
    }

    internal bool IsPrefixOf(string text)
    {
        if (text.Length < Length) return false;
        Span<byte> streamed = stackalloc byte[32];
        Span<byte> final = stackalloc byte[32];
        _hash.GetCurrentHash(streamed);
        SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan(0, Length)), final);
        return streamed.SequenceEqual(final);
    }

    internal void Replace(string text)
    {
        Span<byte> discarded = stackalloc byte[32];
        _hash.GetHashAndReset(discarded);
        Length = 0;
        Append(text);
    }

    public void Dispose() => _hash.Dispose();
}
