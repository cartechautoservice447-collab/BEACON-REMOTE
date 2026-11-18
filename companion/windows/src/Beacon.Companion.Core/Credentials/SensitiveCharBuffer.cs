using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Beacon.Companion.Core.Credentials;

/// <summary>
/// Holds secret character data in a pinned array allocated on the Pinned Object Heap (POH)
/// so the CLR garbage collector never relocates copies during heap compaction, and zeroes
/// the buffer deterministically via <see cref="CryptographicOperations.ZeroMemory"/> on <see cref="Dispose"/>.
/// <para>
/// Important CLR limitation: Any secret that originates as an immutable <see cref="string"/> (for example,
/// from a UI text control) cannot be reliably zeroed in managed memory before garbage collection.
/// Callers should prefer <see cref="FromUtf8Bytes"/> or <see cref="FromChars"/> to avoid creating
/// intermediate <see cref="string"/> instances.
/// </para>
/// </summary>
public sealed class SensitiveCharBuffer : IDisposable
{
    private readonly char[] _pinnedChars;
    private int _disposed;

    private SensitiveCharBuffer(char[] pinnedChars)
    {
        _pinnedChars = pinnedChars;
    }

    public int Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _pinnedChars.Length;
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ReadOnlySpan<char> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _pinnedChars;
        }
    }

    public static SensitiveCharBuffer FromChars(ReadOnlySpan<char> source)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("Secret buffer cannot be empty.", nameof(source));
        }

        var pinned = GC.AllocateArray<char>(source.Length, pinned: true);
        source.CopyTo(pinned);
        return new SensitiveCharBuffer(pinned);
    }

    public static SensitiveCharBuffer FromUtf8Bytes(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.IsEmpty)
        {
            throw new ArgumentException("Secret UTF-8 buffer cannot be empty.", nameof(utf8Bytes));
        }

        var charCount = Encoding.UTF8.GetCharCount(utf8Bytes);
        var pinned = GC.AllocateArray<char>(charCount, pinned: true);
        try
        {
            var written = Encoding.UTF8.GetChars(utf8Bytes, pinned);
            if (written != charCount)
            {
                throw new InvalidOperationException("UTF-8 secret character decoding length mismatch.");
            }

            return new SensitiveCharBuffer(pinned);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pinned.AsSpan()));
            throw;
        }
    }

    /// <summary>
    /// Encodes the secret characters into a caller-provided UTF-8 buffer without allocating a <see cref="string"/>.
    /// </summary>
    public int CopyToUtf8(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Encoding.UTF8.GetBytes(_pinnedChars, destination);
    }

    public int GetUtf8ByteCount()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Encoding.UTF8.GetByteCount(_pinnedChars);
    }

    /// <summary>
    /// Never returns the underlying secret characters so accidental logging or string formatting is safe.
    /// </summary>
    public override string ToString() => "[REDACTED]";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_pinnedChars.AsSpan()));
        }
    }
}
