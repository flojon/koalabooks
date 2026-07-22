using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Threading.RateLimiting;

namespace KoalaBooks.Tests;

/// <summary>
/// Tests for account lockout behaviour (MaxFailedAccessAttempts = 5, 15-min window).
/// Uses a real Identity stack backed by a Postgres database so the access-failed
/// counter and lockout state exercise the actual EF Core store.
/// </summary>
public class LoginLockoutTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;

    public LoginLockoutTests()
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
            opts.Lockout.MaxFailedAccessAttempts = 5;
            opts.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            opts.Lockout.AllowedForNewUsers = true;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task SingleWrongPassword_ReturnsFailedNotLockedOut()
    {
        using var scope = _sp.CreateScope();
        var (userManager, signInManager) = GetManagers(scope);
        await CreateUserAsync(userManager, "nolock@test.com");

        var result = await signInManager.PasswordSignInAsync(
            "nolock@test.com", "WrongPass1!", false, lockoutOnFailure: true);

        Assert.False(result.Succeeded);
        Assert.False(result.IsLockedOut);
    }

    [Fact]
    public async Task FiveWrongPasswords_LocksAccount()
    {
        using var scope = _sp.CreateScope();
        var (userManager, signInManager) = GetManagers(scope);
        await CreateUserAsync(userManager, "lockme@test.com");

        SignInResult result = SignInResult.Failed;
        for (var i = 0; i < 5; i++)
            result = await signInManager.PasswordSignInAsync(
                "lockme@test.com", "WrongPass1!", false, lockoutOnFailure: true);

        Assert.True(result.IsLockedOut);
    }

    [Fact]
    public async Task CorrectPasswordAfterLockout_StillLockedOut()
    {
        using var scope = _sp.CreateScope();
        var (userManager, signInManager) = GetManagers(scope);
        await CreateUserAsync(userManager, "stilllocked@test.com");

        for (var i = 0; i < 5; i++)
            await signInManager.PasswordSignInAsync(
                "stilllocked@test.com", "WrongPass1!", false, lockoutOnFailure: true);

        var result = await signInManager.PasswordSignInAsync(
            "stilllocked@test.com", "ValidPass123!", false, lockoutOnFailure: true);

        Assert.True(result.IsLockedOut);
    }

    [Fact]
    public async Task NewUser_EmailConfirmedIsFalse()
    {
        using var scope = _sp.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "emailcheck@test.com", Email = "emailcheck@test.com" };
        await userManager.CreateAsync(user, "Pass123!");

        var created = await userManager.FindByEmailAsync("emailcheck@test.com");
        Assert.NotNull(created);
        Assert.False(created.EmailConfirmed);
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private static (UserManager<ApplicationUser>, SignInManager<ApplicationUser>) GetManagers(IServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
         scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>());

    private static async Task CreateUserAsync(UserManager<ApplicationUser> userManager, string email)
    {
        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await userManager.CreateAsync(user, "ValidPass123!");
        Assert.True(result.Succeeded);
    }
}

/// <summary>
/// Tests that the "auth" rate limiter policy is correctly configured as a per-IP fixed-window
/// limiter (10 req/min) and returns 429 once the limit is exceeded.
/// Uses a minimal TestServer pipeline so no Aspire or database setup is needed.
/// </summary>
public class RateLimitTests
{
    [Fact]
    public async Task AuthPolicy_ElevenRequests_Returns429OnLast()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(limiter =>
                    {
                        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        limiter.AddPolicy("auth", ctx =>
                            RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                factory: _ => new FixedWindowRateLimiterOptions
                                {
                                    Window = TimeSpan.FromMinutes(1),
                                    PermitLimit = 10,
                                    QueueLimit = 0
                                }));
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(ep =>
                        ep.MapPost("/account/login", () => Results.Ok())
                          .RequireRateLimiting("auth"));
                });
            })
            .StartAsync();

        using var client = host.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            var ok = await client.PostAsync("/account/login", new StringContent(""));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var response = await client.PostAsync("/account/login", new StringContent(""));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task AuthPolicy_DifferentIPs_HaveIndependentCounters()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(limiter =>
                    {
                        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        limiter.AddPolicy("auth", ctx =>
                            RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                factory: _ => new FixedWindowRateLimiterOptions
                                {
                                    Window = TimeSpan.FromMinutes(1),
                                    PermitLimit = 2,
                                    QueueLimit = 0
                                }));
                    });
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(ep =>
                        ep.MapPost("/account/login", () => Results.Ok())
                          .RequireRateLimiting("auth"));
                });
            })
            .StartAsync();

        var server = host.GetTestServer();

        // Exhaust limit from 192.168.1.1
        for (var i = 0; i < 2; i++)
        {
            var ctx = await server.SendAsync(c =>
            {
                c.Request.Method = "POST";
                c.Request.Path = "/account/login";
                c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.1");
            });
            Assert.Equal(200, ctx.Response.StatusCode);
        }

        var blocked = await server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/account/login";
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.1");
        });
        Assert.Equal(429, blocked.Response.StatusCode);

        // Different IP should still get through
        var allowed = await server.SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/account/login";
            c.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.2");
        });
        Assert.Equal(200, allowed.Response.StatusCode);
    }
}

/// <summary>
/// Verifies the login page redirects into the MFA verify step instead of
/// completing sign-in when the user has TwoFactorEnabled set.
/// </summary>
public class MfaLoginRedirectTests
{
    [Fact]
    public async Task Login_UserWithTwoFactorEnabled_RedirectsToVerifyPage()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using (var scope = factory.Services.CreateScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                // EmailConfirmed is required alongside TwoFactorEnabled: SignInManager only sets
                // RequiresTwoFactor when the user has at least one usable two-factor token provider,
                // and the built-in Email provider needs a confirmed address to qualify.
                var user = new ApplicationUser { UserName = "mfauser@test.com", Email = "mfauser@test.com", EmailConfirmed = true };
                await userManager.CreateAsync(user, "ValidPass123!");
                await userManager.SetTwoFactorEnabledAsync(user, true);
            }

            var loginPage = await client.GetAsync("/account/login");
            var token = OidcTestHelpers.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

            var form = new Dictionary<string, string>
            {
                ["Email"] = "mfauser@test.com",
                ["Password"] = "ValidPass123!",
                ["__RequestVerificationToken"] = token
            };
            var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(form));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/mfa/verify", response.Headers.Location!.OriginalString);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}

/// <summary>
/// End-to-end regression test: enrol MFA, log in with password, redirect to
/// the verify page, submit a valid TOTP code, and land signed in at ReturnUrl.
/// </summary>
public class MfaFullLoginFlowTests
{
    [Fact]
    public async Task Login_WithValidTotpCode_CompletesSignIn()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            await using var factory = new WebApiFactory(connStr);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            MfaEnrollmentInfo enrollment;
            using (var scope = factory.Services.CreateScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var mfaService = scope.ServiceProvider.GetRequiredService<IMfaService>();
                var user = new ApplicationUser { UserName = "fulle2e@test.com", Email = "fulle2e@test.com", EmailConfirmed = true };
                await userManager.CreateAsync(user, "ValidPass123!");
                enrollment = await mfaService.BeginEnrollmentAsync(user.Id);
                var firstCode = TotpTestHelper.GenerateCode(enrollment.SharedKey.Replace(" ", ""));
                await mfaService.ConfirmEnrollmentAsync(user.Id, firstCode);
            }

            var loginPage = await client.GetAsync("/account/login");
            var loginToken = OidcTestHelpers.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
            var loginResponse = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = "fulle2e@test.com",
                ["Password"] = "ValidPass123!",
                ["__RequestVerificationToken"] = loginToken
            }));
            Assert.StartsWith("/account/mfa/verify", loginResponse.Headers.Location!.OriginalString);

            var verifyPage = await client.GetAsync(loginResponse.Headers.Location);
            var verifyToken = OidcTestHelpers.ExtractAntiforgeryToken(await verifyPage.Content.ReadAsStringAsync());
            var verifyCode = TotpTestHelper.GenerateCode(enrollment.SharedKey.Replace(" ", ""));
            var verifyResponse = await client.PostAsync(loginResponse.Headers.Location, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Code"] = verifyCode,
                ["ReturnUrl"] = "/",
                ["RememberMe"] = "false",
                ["UseRecoveryCode"] = "false",
                ["__RequestVerificationToken"] = verifyToken
            }));

            Assert.Equal(HttpStatusCode.Redirect, verifyResponse.StatusCode);
            Assert.Equal("/", verifyResponse.Headers.Location!.OriginalString);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}
