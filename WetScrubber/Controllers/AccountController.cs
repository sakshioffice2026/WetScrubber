using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WetScrubber.Database;
using WetScrubber.Helpers;
using WetScrubber.Models;

namespace WetScrubber.Controllers
{
    /// <summary>
    /// Handles Login, Register, Logout.
    /// Uses custom PasswordHash + PasswordSalt — NO ASP.NET Core Identity.
    /// Session is used to store logged-in user info.
    /// </summary>
    public class AccountController : Controller
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<AccountController> _logger;

        public AccountController(ApplicationDbContext dbContext, ILogger<AccountController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        // ── GET /Account/Login ────────────────────────────────────
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            // Already logged in → go to dashboard
            if (HttpContext.Session.GetInt32("UserId") != null)
                return RedirectToAction("Index", "Dashboard");

            var model = new LoginViewModel { ReturnUrl = returnUrl };
            return View(model);
        }

        // ── POST /Account/Login ───────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // 1. Find user by email
            var user = await _dbContext.Users
                                       .Include(u => u.Role)
                                       .FirstOrDefaultAsync(u => u.Email == model.Email && u.IsActive);

            // 2. User not found
            if (user == null)
            {
                ModelState.AddModelError(string.Empty, "Invalid email or password.");
                return View(model);
            }

            // 3. Verify password
            bool isPasswordValid = PasswordHelper.VerifyPassword(model.Password, user.PasswordHash, user.PasswordSalt);

            if (!isPasswordValid)
            {
                
                ModelState.AddModelError(string.Empty, "Invalid email or password.");
                return View(model);
            }

            // 4. Update LastLoginAt
            user.LastLoginAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            // 5. Save user info in Session
            HttpContext.Session.SetInt32("UserId", user.UserId);
            HttpContext.Session.SetString("UserName", user.FullName);
            HttpContext.Session.SetString("UserEmail", user.Email);
            HttpContext.Session.SetString("UserRole", user.Role?.RoleName ?? "Engineer");

           
            _logger.LogInformation("User {Email} logged in.", user.Email);

            // 7. Redirect
            if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                return Redirect(model.ReturnUrl);

            return RedirectToAction("Index", "Dashboard");
        }

        // ── GET /Account/Register ─────────────────────────────────
        [HttpGet]
        public IActionResult Register()
        {
            if (HttpContext.Session.GetInt32("UserId") != null)
                return RedirectToAction("Index", "Dashboard");

            return View(new RegisterViewModel());
        }

        // ── POST /Account/Register ────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // 1. Check if email already exists
            bool emailExists = await _dbContext.Users.AnyAsync(u => u.Email == model.Email);
            if (emailExists)
            {
                ModelState.AddModelError("Email", "This email is already registered.");
                return View(model);
            }

            // 2. Get default role (Engineer)
            var engineerRole = await _dbContext.Roles.FirstOrDefaultAsync(r => r.RoleName == "Engineer");
            if (engineerRole == null)
            {
                ModelState.AddModelError(string.Empty, "Default role not found. Please contact admin.");
                return View(model);
            }

            // 3. Hash password
            string salt = PasswordHelper.GenerateSalt();
            string hash = PasswordHelper.HashPassword(model.Password, salt);

            // 4. Create user
            var user = new User
            {
                FullName = model.FullName,
                UserName = model.Email,
                Email = model.Email,
                PasswordHash = hash,
                PasswordSalt = salt,
                JobTitle = model.JobTitle,
                Department = model.Department,
                Company = model.Company,
                RoleId = engineerRole.RoleId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("New user registered: {Email}", user.Email);

            // 5. Auto-login after registration
            HttpContext.Session.SetInt32("UserId", user.UserId);
            HttpContext.Session.SetString("UserName", user.FullName);
            HttpContext.Session.SetString("UserEmail", user.Email);
            HttpContext.Session.SetString("UserRole", engineerRole.RoleName);

            TempData["Success"] = $"Welcome, {user.FullName}! Your account has been created.";
            return RedirectToAction("Index", "Dashboard");
        }

        // ── POST /Account/Logout ──────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            _logger.LogInformation("User logged out.");
            return RedirectToAction("Login", "Account");
        }

        // ── GET /Account/ForgotPassword ───────────────────────────
        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View(new ForgotPasswordViewModel());
        }

        // ── POST /Account/ForgotPassword ──────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == model.Email);

            // Always show success — don't reveal if email exists (security best practice)
            TempData["Info"] = "If this email is registered, you will receive a reset link shortly.";
            return RedirectToAction(nameof(Login));
        }

        // ── GET /Account/AccessDenied ─────────────────────────────
        [HttpGet]
        public IActionResult AccessDenied()
        {
            return View();
        }

       
    }
}
