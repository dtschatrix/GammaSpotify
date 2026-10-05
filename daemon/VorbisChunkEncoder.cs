using OggVorbisEncoder;

namespace SpotifyRadio;

/// <summary>
/// Encodes blocks of interleaved 16-bit PCM into self-contained OGG Vorbis files that decode to
/// exactly the samples given, so files played back to back are continuous.
/// </summary>
public static class VorbisChunkEncoder
{
    private const int Block = 512;

    /// <summary>
    /// The encoder library drops the first samples it is given (normally 1024) instead of priming
    /// itself, so every chunk is encoded with that much of the preceding audio in front of it.
    /// </summary>
    public const int DefaultLeadFrames = 1024;

    /// <param name="history">Audio that came right before <paramref name="pcm"/> (same format); may be shorter than needed or empty.</param>
    /// <param name="pcm">Interleaved signed 16-bit little-endian samples to encode.</param>
    /// <param name="inChannels">Channels in the input (1 or 2).</param>
    /// <param name="outChannels">1 mixes stereo down to mono (needed for 3D sound in the game); 2 keeps stereo.</param>
    /// <param name="quality">Vorbis quality, 0..1.</param>
    public static byte[] Encode(ReadOnlySpan<byte> history, ReadOnlySpan<byte> pcm, int inChannels, int outChannels, int sampleRate, float quality)
    {
        var frames = pcm.Length / (2 * inChannels);
        var lead = DefaultLeadFrames;
        byte[] best = Array.Empty<byte>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            best = EncodeOnce(history, pcm, inChannels, outChannels, sampleRate, quality, lead, out var decodedFrames);
            if (decodedFrames == frames) return best;
            // The file would decode to a different length: adjust the lead-in by the difference and retry.
            lead += frames - (int)decodedFrames;
            if (lead < 0) lead = 0;
        }
        throw new InvalidOperationException("Could not encode a chunk of the exact length");
    }

    private static byte[] EncodeOnce(ReadOnlySpan<byte> history, ReadOnlySpan<byte> pcm, int inChannels, int outChannels,
        int sampleRate, float quality, int leadFrames, out long decodedFrames)
    {
        var frameBytes = 2 * inChannels;
        var frames = pcm.Length / frameBytes;
        var total = leadFrames + frames;
        var samples = new float[outChannels][];
        for (var c = 0; c < outChannels; c++) samples[c] = new float[total];

        // lead-in: the tail of the history, zero-padded at the front if there is not enough of it
        var haveLead = Math.Min(leadFrames, history.Length / frameBytes);
        Fill(samples, leadFrames - haveLead, history[(history.Length - haveLead * frameBytes)..], inChannels, outChannels);
        Fill(samples, leadFrames, pcm, inChannels, outChannels);

        using var output = new MemoryStream();
        var info = VorbisInfo.InitVariableBitRate(outChannels, sampleRate, quality);
        var ogg = new OggStream(Random.Shared.Next());
        long lastGranule = 0;

        void Flush(bool force)
        {
            while (ogg.PageOut(out OggPage page, force))
            {
                output.Write(page.Header, 0, page.Header.Length);
                output.Write(page.Body, 0, page.Body.Length);
                var granule = BitConverter.ToInt64(page.Header, 6);
                if (granule > lastGranule) lastGranule = granule;
            }
        }

        // No comment tags: the game reads the first Vorbis comment as its own binary header.
        ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        Flush(force: true);

        var state = ProcessingState.Create(info);
        void Drain()
        {
            while (!ogg.Finished && state.PacketOut(out OggPacket packet))
            {
                ogg.PacketIn(packet);
                Flush(force: false);
            }
        }

        for (var read = 0; read < total; read += Block)
        {
            state.WriteData(samples, Math.Min(Block, total - read), read);
            Drain();
        }
        state.WriteEndOfStream();
        Drain();
        Flush(force: true);

        decodedFrames = lastGranule; // the final granule position is the number of frames a decoder will output
        return output.ToArray();
    }

    private static void Fill(float[][] dest, int at, ReadOnlySpan<byte> pcm, int inChannels, int outChannels)
    {
        var frames = pcm.Length / (2 * inChannels);
        for (var i = 0; i < frames; i++)
        {
            var o = i * 2 * inChannels;
            float l = (short)(pcm[o] | (pcm[o + 1] << 8)) / 32768f;
            float r = inChannels == 2 ? (short)(pcm[o + 2] | (pcm[o + 3] << 8)) / 32768f : l;
            if (outChannels == 1) dest[0][at + i] = (l + r) * 0.5f;
            else { dest[0][at + i] = l; dest[1][at + i] = r; }
        }
    }
}
