using System.IO;
using System.Text;
using Img = SixLabors.ImageSharp.Image;

namespace KanonBot;

public static partial class Utils
{
    public static string Byte2File(string fileName, byte[] buffer)
    {
        using (var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
        {
            fs.Write(buffer, 0, buffer.Length);
        }
        return Path.GetFullPath(fileName);
        ;
    }

    public static Stream LoadFile2ReadStream(string filePath)
    {
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return fs;
    }

    public static async Task<byte[]> LoadFile2Byte(string filePath)
    {
        using var fs = LoadFile2ReadStream(filePath);
        byte[] bt = new byte[fs.Length];
        var mem = new Memory<Byte>(bt);
        await fs.ReadExactlyAsync(mem);
        fs.Close();
        return mem.ToArray();
    }

    public static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        return builder.ToString();
    }

    public static async Task<string?> CacheRemoteImage(string cacheRoot, string url, string key)
    {
        return await RemoteImageCache.Get(cacheRoot, url, key);
    }
}
