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

        var userManagement = app.MapGroup("/api/users")
            .RequireAuthorization(policy => policy.RequireRole(HistorianRoles.Administrator));

        userManagement.MapGet(
            "",
            async (IUserRepository users, CancellationToken cancellationToken) =>
                Results.Ok((await users.ListAsync(cancellationToken)).Select(ToManagedUser)));

        userManagement.MapPost(
            "",
            async (
                CreateUserRequest request,
                IUserRepository users,
                IPasswordHasher<ApplicationUser> passwordHasher,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var validationError = Validate(
                    request.UserName,
                    request.DisplayName,
                    request.Password);
                if (validationError is not null)
                    return Results.BadRequest(new { error = validationError });
                if (!IsKnownRole(request.Role))
                    return Results.BadRequest(new { error = "Perfil de acesso inválido." });
                if (await users.FindByUserNameAsync(request.UserName, cancellationToken) is not null)
                    return Results.Conflict(new { error = "Já existe um usuário com este identificador." });

                var createdAtUtc = clock.GetUtcNow();
                var placeholder = new ApplicationUser(
                    0,
                    request.UserName.Trim(),
                    request.DisplayName.Trim(),
                    string.Empty,
                    request.Role,
                    true,
                    createdAtUtc,
                    null);
                var passwordHash = passwordHasher.HashPassword(placeholder, request.Password);
                var id = await users.CreateAsync(
                    placeholder.UserName,
                    placeholder.DisplayName,
                    passwordHash,
                    placeholder.Role,
                    createdAtUtc,
                    cancellationToken);
                return Results.Created(
                    $"/api/users/{id}",
                    ToManagedUser(placeholder with { Id = id, PasswordHash = passwordHash }));
            });

        userManagement.MapPut(
            "/{id:long}",
            async (
                long id,
                UpdateUserRequest request,
                ClaimsPrincipal principal,
                IUserRepository users,
                CancellationToken cancellationToken) =>
            {
                var target = await users.FindByIdAsync(id, cancellationToken);
                if (target is null)
                    return Results.NotFound(new { error = "Usuário não encontrado." });
                if (string.IsNullOrWhiteSpace(request.DisplayName))
                    return Results.BadRequest(new { error = "Nome completo é obrigatório." });
                if (!IsKnownRole(request.Role))
                    return Results.BadRequest(new { error = "Perfil de acesso inválido." });

                var currentUserId = GetUserId(principal);
                if (currentUserId == id &&
                    (!request.IsActive || request.Role != target.Role))
                {
                    return Results.Conflict(new
                    {
                        error = "A sessão atual não pode ser desativada nem ter o perfil alterado."
                    });
                }

                var removesActiveAdministrator =
                    target.IsActive &&
                    target.Role == HistorianRoles.Administrator &&
                    (!request.IsActive || request.Role != HistorianRoles.Administrator);
                if (removesActiveAdministrator &&
                    await users.CountActiveAdministratorsAsync(cancellationToken) <= 1)
                {
                    return Results.Conflict(new
                    {
                        error = "Mantenha pelo menos um administrador ativo."
                    });
                }

                await users.UpdateAsync(
                    id,
                    request.DisplayName,
                    request.Role,
                    request.IsActive,
                    cancellationToken);
                var updated = await users.FindByIdAsync(id, cancellationToken);
                return Results.Ok(ToManagedUser(updated!));
            });

        userManagement.MapPut(
            "/{id:long}/password",
            async (
                long id,
                ResetPasswordRequest request,
                IUserRepository users,
                IPasswordHasher<ApplicationUser> passwordHasher,
                CancellationToken cancellationToken) =>
            {
                if (request.Password is null || request.Password.Length < 8)
                    return Results.BadRequest(new { error = "A senha deve possuir pelo menos 8 caracteres." });
                var target = await users.FindByIdAsync(id, cancellationToken);
                if (target is null)
                    return Results.NotFound(new { error = "Usuário não encontrado." });

                var passwordHash = passwordHasher.HashPassword(target, request.Password);
                await users.UpdatePasswordHashAsync(id, passwordHash, cancellationToken);
                return Results.NoContent();
            });
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
        permissions = GetPermissions(user.Role)
    };

    private static object ToManagedUser(ApplicationUser user) => new
    {
        user.Id,
        user.UserName,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.CreatedAtUtc,
        user.LastLoginAtUtc
    };

    private static string[] GetPermissions(string role)
    {
        var permissions = new List<string>
        {
            "dashboard.view",
            "status.view",
            "alarms.view",
            "commands.view",
            "history.view",
            "reports.generate"
        };
        if (role == HistorianRoles.Administrator)
        {
            permissions.AddRange(
            [
                "users.view",
                "users.create",
                "users.edit",
                "users.disable",
                "users.reset-password",
                "updates.view",
                "updates.check",
                "updates.download",
                "updates.install"
            ]);
        }
        return [.. permissions];
    }

    private static long? GetUserId(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    private static bool IsKnownRole(string? role) =>
        role is HistorianRoles.Viewer or HistorianRoles.Administrator;

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
    private sealed record CreateUserRequest(
        string UserName,
        string DisplayName,
        string Password,
        string Role);
    private sealed record UpdateUserRequest(string DisplayName, string Role, bool IsActive);
    private sealed record ResetPasswordRequest(string Password);
}
