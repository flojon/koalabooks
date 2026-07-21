using KoalaBooks.Infrastructure.Data;

namespace KoalaBooks.Application.Services;

public interface IMfaService
{
    Task<MfaEnrollmentInfo> BeginEnrollmentAsync(ApplicationUser user);
    Task<MfaConfirmResult> ConfirmEnrollmentAsync(ApplicationUser user, string code);
    Task<bool> DisableAsync(ApplicationUser user, string password);
}

public record MfaEnrollmentInfo(string SharedKey, string QrCodeDataUri);

public record MfaConfirmResult(bool Succeeded, string? Error, IReadOnlyList<string> RecoveryCodes);
