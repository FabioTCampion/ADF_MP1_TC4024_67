using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

public static partial class AuthenticationEndpoints
{
    public static void MapHistorianAuthentication(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/status", async (IUserRepository users, CancellationToken cancellationToken) =>
            Results.Ok(new { requiresSetup = await users.CountAsync(cancellationToken) == 0 }));

        auth.MapGet(
            "/me",
            async (
                ClaimsPrincipal principal,
                IUserRepository users,
                CancellationToken cancellationToken) =>
            {
                if (principal.Identity?.IsAuthenticated != true)
                    return Results.Unauthorized();

                var identifier = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                var user = long.TryParse(identifier, out var id)
                    ? await users.FindByIdAsync(id, cancellationToken)
                    : null;
                return user is null || !user.IsActive
                    ? Results.Unauthorized()
                    : Results.Ok(ToCurrentUser(user));
            });

        auth.MapPost(
            "/setup",
            async (
                SetupRequest request,
                IUserRepository users,
                IPasswordHasher<ApplicationUser> passwordHasher,
                TimeProvider clock,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                if (await users.CountAsync(cancellationToken) != 0)
                    return Results.Conflict(new { error = "O administrador inicial já foi criado." });

                var validationError = Validate(request.UserName, request.DisplayName, request.Password);
                if (validationError is not null)
                    return Results.BadRequest(new { error = validationError });

                var placeholder = new ApplicationUser(
                    0,
                    request.UserName.Trim(),
                    request.DisplayName.Trim(),
                    string.Empty,
                    HistorianRoles.Administrator,
                    true,
                    clock.GetUtcNow(),
                    null);
                var passwordHash = passwordHasher.HashPassword(placeholder, request.Password);
                var id = await users.CreateAsync(
                    placeholder.UserName,
                    placeholder.DisplayName,
                    passwordHash,
                    placeholder.Role,
                    placeholder.CreatedAtUtc,
                    cancellationToken);
                var user = placeholder with { Id = id, PasswordHash = passwordHash };
                await SignInAsync(context, user);
                return Results.Ok(ToCurrentUser(user));
            })
            .RequireRateLimiting("authentication");

        auth.MapPost(
            "/login",
            async (
                LoginRequest request,
                IUserRepository users,
                IPasswordHasher<ApplicationUser> passwordHasher,
                TimeProvider clock,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var user = await users.FindByUserNameAsync(
                    request.UserName ?? string.Empty,
                    cancellationToken);
                if (user is null ||
                    !user.IsActive ||
                    passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty) ==
                    PasswordVerificationResult.Failed)
                {
                    return Results.Unauthorized();
                }

                await users.MarkLoginAsync(user.Id, clock.GetUtcNow(), cancellationToken);
                await SignInAsync(context, user);
                return Results.Ok(ToCurrentUser(user));
            })
            .RequireRateLimiting("authentication");

        auth.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).RequireAuthorization();
    }

    private static async Task SignInAsync(HttpContext context, ApplicationUser user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim("display_name", user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role)
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
    }

    private static object ToCurrentUser(ApplicationUser user) => new
    {
        user.Id,
        user.UserName,
        user.DisplayName,
        user.Role,
        permissions = new[]
        {
            "dashboard.view",
            "status.view",
            "alarms.view",
            "commands.view",
            "history.view"
        }
    };

    private static string? Validate(string userName, string displayName, string password)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "Nome completo é obrigatório.";
        if (!UserNamePattern().IsMatch(userName?.Trim() ?? string.Empty))
            return "O usuário deve ter de 3 a 50 caracteres: letras, números, ponto, hífen ou sublinhado.";
        if (password is null || password.Length < 8)
            return "A senha deve possuir pelo menos 8 caracteres.";
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();

    private sealed record SetupRequest(string UserName, string DisplayName, string Password);
    private sealed record LoginRequest(string UserName, string Password);
}
