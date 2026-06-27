using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Trustlist.Api.Auth;
using Trustlist.Api.Dtos;

namespace Trustlist.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager,
    TokenService tokenService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var existing = await userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            return Conflict(new { message = "A user with this email already exists." });

        var user = new IdentityUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        var (token, expiresAt) = tokenService.CreateToken(user);
        return Created($"/api/auth/{Uri.EscapeDataString(user.Email!)}", new AuthResponse(token, user.Email!, expiresAt));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized(new { message = "Invalid credentials." });

        var valid = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
        if (!valid.Succeeded)
            return Unauthorized(new { message = "Invalid credentials." });

        var (token, expiresAt) = tokenService.CreateToken(user);
        return Ok(new AuthResponse(token, user.Email!, expiresAt));
    }
}
