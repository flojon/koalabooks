namespace KoalaBooks.Domain.Interfaces;

public interface IMfaService
{
    Task<bool> IsEnabledAsync(string userId);
    Task<MfaEnrollmentInfo> BeginEnrollmentAsync(string userId);
    Task<MfaConfirmResult> ConfirmEnrollmentAsync(string userId, string code);
    Task<bool> DisableAsync(string userId, string password);
}

public record MfaEnrollmentInfo(string SharedKey, string QrCodeDataUri);

public record MfaConfirmResult(bool Succeeded, string? Error, IReadOnlyList<string> RecoveryCodes);
