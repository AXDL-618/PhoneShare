using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhoneShareReceiver;

public static class SecurityUtil
{
    public static string CreateToken(int bytes = 32)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    public static string SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "unnamed.bin";

        fileName = Path.GetFileName(fileName.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "unnamed.bin";

        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        fileName = fileName.Replace("..", "_");
        return fileName;
    }

    public static string UniquePath(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        var safe = SafeFileName(fileName);
        var full = Path.Combine(folder, safe);
        if (!File.Exists(full)) return full;

        var name = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var i = 1; i < 100000; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{name}-{Guid.NewGuid():N}{ext}");
    }
}
