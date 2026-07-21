using KoalaBooks.Application.Services;

namespace KoalaBooks.Tests;

public class QrCodeGeneratorTests
{
    [Fact]
    public void GenerateDataUri_ReturnsPngDataUri()
    {
        var dataUri = QrCodeGenerator.GenerateDataUri("otpauth://totp/KoalaBooks:user@example.com?secret=ABC&issuer=KoalaBooks&digits=6");

        Assert.StartsWith("data:image/png;base64,", dataUri);

        var base64 = dataUri["data:image/png;base64,".Length..];
        var bytes = Convert.FromBase64String(base64);

        // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(pngSignature, bytes[..8]);
    }
}
