namespace MnaiWork.Api.Generation;

/// <summary>
/// Reads pixel dimensions from common image formats (PNG, JPEG, GIF, BMP) by parsing headers,
/// avoiding a dependency on System.Drawing / ImageSharp.
/// </summary>
public static class ImageDimensions
{
    public static (int Width, int Height) Read(byte[] data)
    {
        try
        {
            if (IsPng(data)) return ReadPng(data);
            if (IsGif(data)) return ReadGif(data);
            if (IsBmp(data)) return ReadBmp(data);
            if (IsJpeg(data)) return ReadJpeg(data);
        }
        catch
        {
            // Fall through to a sane default.
        }
        return (1024, 768);
    }

    private static bool IsPng(byte[] d) => d.Length > 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
    private static bool IsGif(byte[] d) => d.Length > 10 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46;
    private static bool IsBmp(byte[] d) => d.Length > 26 && d[0] == 0x42 && d[1] == 0x4D;
    private static bool IsJpeg(byte[] d) => d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8;

    private static (int, int) ReadPng(byte[] d)
    {
        int w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
        int h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
        return (w, h);
    }

    private static (int, int) ReadGif(byte[] d)
    {
        int w = d[6] | (d[7] << 8);
        int h = d[8] | (d[9] << 8);
        return (w, h);
    }

    private static (int, int) ReadBmp(byte[] d)
    {
        int w = d[18] | (d[19] << 8) | (d[20] << 16) | (d[21] << 24);
        int h = d[22] | (d[23] << 8) | (d[24] << 16) | (d[25] << 24);
        return (w, Math.Abs(h));
    }

    private static (int, int) ReadJpeg(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            // SOF markers carry the frame dimensions.
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                int h = (d[i + 5] << 8) | d[i + 6];
                int w = (d[i + 7] << 8) | d[i + 8];
                return (w, h);
            }
            int len = (d[i + 2] << 8) | d[i + 3];
            i += 2 + len;
        }
        return (1024, 768);
    }
}
