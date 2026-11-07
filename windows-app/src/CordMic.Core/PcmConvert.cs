namespace CordMic.Core;

/// <summary>s16le → IEEE float32 conversion used by the WASAPI mixer path (no extra resample).</summary>
public static class PcmConvert
{
    /// <summary>
    /// Convert <paramref name="frames"/> of interleaved s16le in <paramref name="buf"/> to float32 in place,
    /// walking backwards so the wider values do not overwrite unread samples. Scale is 1/32768.
    /// </summary>
    public static void ExpandS16ToF32(byte[] buf, int frames, int srcCh, int dstCh)
    {
        if (frames <= 0 || srcCh <= 0 || dstCh <= 0) return;
        for (int f = frames - 1; f >= 0; f--)
        {
            int srcOff = f * srcCh * 2;
            short left = (short)(buf[srcOff] | (buf[srcOff + 1] << 8));
            short right = srcCh > 1 ? (short)(buf[srcOff + 2] | (buf[srcOff + 3] << 8)) : left;
            float fl = left / 32768f, fr = right / 32768f;
            int dstOff = f * dstCh * 4;
            WriteF32(buf, dstOff, fl);
            if (dstCh > 1) WriteF32(buf, dstOff + 4, fr);
        }
    }

    public static void WriteF32(byte[] buf, int off, float v)
    {
        uint bits = (uint)BitConverter.SingleToInt32Bits(v);
        buf[off] = (byte)bits; buf[off + 1] = (byte)(bits >> 8);
        buf[off + 2] = (byte)(bits >> 16); buf[off + 3] = (byte)(bits >> 24);
    }
}
