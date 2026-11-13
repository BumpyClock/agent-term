namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    /// <summary>
    /// Kitty image storage per screen. libghostty-vt defaults to 10 MB, which one full-HD RGBA image fills. 64 MB
    /// holds several screenshots while bounding memory: decoded pixels live natively, as an owned frame copy, and
    /// as device bitmaps for drawn images.
    /// </summary>
    public const ulong DefaultImageStorageLimit = 64UL * 1024 * 1024;

    private void ConfigureImages()
    {
        _terminal.SetHostCapabilities(new TerminalHostCapabilities(KittyImages: true));
    }

    /// <summary>
    /// Sets the cell size in device pixels. Kitty images are positioned and scaled from it, and cell-size reports
    /// answer with it. Replies the size change triggers, such as an in-band resize report, go to the shell.
    /// </summary>
    public void SetCellPixelSize(uint width, uint height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _terminal.SetCellPixelSize(width, height);
            foreach (var reply in _terminal.TakeResponses())
                if (_started && _stopTask is null) _connection.SendReply(reply);
        }
    }

    /// <summary>Bytes of decoded image pixels the session holds for the active screen.</summary>
    public long ImageCacheBytes { get { lock (_renderGate) return _disposeTask is null ? _terminal.ImageCacheBytes : 0; } }
}
