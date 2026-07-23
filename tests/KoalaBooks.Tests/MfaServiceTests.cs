using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

/// <summary>
/// Exercises MfaService against a real Identity + Postgres stack so
/// UserManager.VerifyTwoFactorTokenAsync runs against the actual AuthenticatorTokenProvider,
/// not a mock — a wrong SHA1/step/digit-count wiring would otherwise pass silently.
/// </summary>
public class MfaServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;

    public MfaServiceTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentUser, LocalCurrentUser>();
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddIdentity<ApplicationUser, IdentityRole>(opts =>
        {
            opts.Password.RequiredLength = 8;
            opts.Password.RequireDigit = true;
            opts.Password.RequireUppercase = true;
            opts.Password.RequireLowercase = true;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();
        services.AddScoped<IMfaService, KoalaBooks.Application.Services.MfaService>();

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task ConfirmEnrollment_WithValidCode_EnablesTwoFactorAndReturnsRecoveryCodes()
    {
        using var scope = _sp.CreateScope();
        var (userManager, mfaService) = GetServices(scope);
        var user = await CreateUserAsync(userManager, "enrol@test.com");

        var enrollment = await mfaService.BeginEnrollmentAsync(user.Id);
        var code = await TotpTestHelper.GenerateCodeAsync(enrollment.SharedKey.Replace(" ", ""));

        var result = await mfaService.ConfirmEnrollmentAsync(user.Id, code);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(10, result.RecoveryCodes.Count);
        Assert.True(await userManager.GetTwoFactorEnabledAsync(user));
    }

    [Fact]
    public async Task ConfirmEnrollment_WithWrongCode_DoesNotEnableTwoFactor()
    {
        using var scope = _sp.CreateScope();
        var (userManager, mfaService) = GetServices(scope);
        var user = await CreateUserAsync(userManager, "wrongcode@test.com");

        await mfaService.BeginEnrollmentAsync(user.Id);
        var result = await mfaService.ConfirmEnrollmentAsync(user.Id, "000000");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.False(await userManager.GetTwoFactorEnabledAsync(user));
    }

    [Fact]
    public async Task Disable_WithCorrectPassword_DisablesTwoFactor()
    {
        using var scope = _sp.CreateScope();
        var (userManager, mfaService) = GetServices(scope);
        var user = await CreateUserAsync(userManager, "disable@test.com");
        var enrollment = await mfaService.BeginEnrollmentAsync(user.Id);
        await mfaService.ConfirmEnrollmentAsync(user.Id, await TotpTestHelper.GenerateCodeAsync(enrollment.SharedKey.Replace(" ", "")));

        var disabled = await mfaService.DisableAsync(user.Id, "ValidPass123!");

        Assert.True(disabled);
        Assert.False(await userManager.GetTwoFactorEnabledAsync(user));
    }

    [Fact]
    public async Task Disable_WithWrongPassword_LeavesTwoFactorEnabled()
    {
        using var scope = _sp.CreateScope();
        var (userManager, mfaService) = GetServices(scope);
        var user = await CreateUserAsync(userManager, "wrongpw@test.com");
        var enrollment = await mfaService.BeginEnrollmentAsync(user.Id);
        await mfaService.ConfirmEnrollmentAsync(user.Id, await TotpTestHelper.GenerateCodeAsync(enrollment.SharedKey.Replace(" ", "")));

        var disabled = await mfaService.DisableAsync(user.Id, "NotThePassword1!");

        Assert.False(disabled);
        Assert.True(await userManager.GetTwoFactorEnabledAsync(user));
    }

    [Fact]
    public async Task IsEnabled_ReflectsTwoFactorState_BeforeAndAfterEnrollment()
    {
        using var scope = _sp.CreateScope();
        var (userManager, mfaService) = GetServices(scope);
        var user = await CreateUserAsync(userManager, "isenabled@test.com");

        Assert.False(await mfaService.IsEnabledAsync(user.Id));

        var enrollment = await mfaService.BeginEnrollmentAsync(user.Id);
        await mfaService.ConfirmEnrollmentAsync(user.Id, await TotpTestHelper.GenerateCodeAsync(enrollment.SharedKey.Replace(" ", "")));

        Assert.True(await mfaService.IsEnabledAsync(user.Id));
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private static (UserManager<ApplicationUser>, IMfaService) GetServices(IServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
         scope.ServiceProvider.GetRequiredService<IMfaService>());

    private static async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> userManager, string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await userManager.CreateAsync(user, "ValidPass123!");
        Assert.True(result.Succeeded);
        return user;
    }

}
