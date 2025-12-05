using System;
using MessangerWeb.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace MessangerWeb.Controllers
{
    public class AccountController : Controller
    {
        private const string AdminEmail = "admin";
        private const string AdminPassword = "123";
        private readonly string connectionString;

        public AccountController(IConfiguration configuration)
        {
            connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        [HttpGet]
        public IActionResult AdminLogin()
        {
            return View();
        }

        [HttpPost]
        public IActionResult AdminLogin(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            if (model.Email.Equals(AdminEmail, StringComparison.OrdinalIgnoreCase)
                && model.Password == AdminPassword)
            {
                HttpContext.Session.SetString("UserType", "Admin");
                HttpContext.Session.SetString("Email", model.Email);

                // Test if session is being set
                var test = HttpContext.Session.GetString("UserType");
                Console.WriteLine($"Session UserType set to: {test}");

                return RedirectToAction("Index", "AdminDashboard");
            }

            ModelState.AddModelError(string.Empty, "Incorrect username or password!");
            return View(model);
        }
        // ================= USER LOGIN =================
        [HttpGet]
        public IActionResult UserLogin()
        {
            return View();
        }

        [HttpPost]
        public IActionResult UserLogin(string Email, string Password)
        {
            ViewBag.Email = Email;

            if (string.IsNullOrWhiteSpace(Email))
            {
                ViewBag.ErrorMessage = "Email is required.";
                return View();
            }

            if (string.IsNullOrWhiteSpace(Password))
            {
                ViewBag.ErrorMessage = "Password is required.";
                return View();
            }

            try
            {
                if (!TestDatabaseConnection())
                {
                    ViewBag.ErrorMessage = "Database connection failed. Please contact administrator.";
                    return View();
                }

                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    string query = @"SELECT ""id"", ""email"", ""firstname"", ""lastname"", ""status"" 
                                     FROM students 
                                     WHERE ""email"" = @Email 
                                     AND ""password"" = @Password 
                                     AND ""status"" = 'Active'";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@Email", Email);
                        command.Parameters.AddWithValue("@Password", Password);

                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                // Store session data
                                HttpContext.Session.SetString("UserType", "User");
                                HttpContext.Session.SetString("UserId", reader["id"].ToString());
                                HttpContext.Session.SetString("Email", reader["email"].ToString());
                                HttpContext.Session.SetString("FirstName", reader["firstname"].ToString());
                                HttpContext.Session.SetString("LastName", reader["lastname"].ToString());
                                HttpContext.Session.SetString("UserName", $"{reader["firstname"]} {reader["lastname"]}");

                                // Clear the saved email from localStorage on successful login
                                TempData["ClearLoginEmail"] = true;

                                return RedirectToAction("Index", "UserDashboard");
                            }
                            else
                            {
                                bool isInactive = CheckIfUserIsInactive(Email, Password);

                                if (isInactive)
                                {
                                    ViewBag.ErrorMessage = "Your account is inactive. Please contact administrator.";
                                }
                                else
                                {
                                    ViewBag.ErrorMessage = "Invalid email or password.";
                                }

                                return View();
                            }
                        }
                    }
                }
            }
            catch (NpgsqlException ex)
            {
                ViewBag.ErrorMessage = $"Database error: {ex.Message}";
                return View();
            }
            catch (Exception ex)
            {
                ViewBag.ErrorMessage = $"Error: {ex.Message}";
                return View();
            }
        }

        // ================= HELPER METHODS =================
        private bool TestDatabaseConnection()
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool CheckIfUserIsInactive(string email, string password)
        {
            try
            {
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();

                    string query = @"SELECT COUNT(*) 
                                     FROM students 
                                     WHERE ""email"" = @Email 
                                     AND ""password"" = @Password 
                                     AND ""status"" != 'Active'";

                    using (var command = new NpgsqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@Email", email ?? "");
                        command.Parameters.AddWithValue("@Password", password ?? "");

                        long result = (long)command.ExecuteScalar();
                        return result > 0;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ================= LOGOUT =================
        public IActionResult Logout()
        {
            var userType = HttpContext.Session.GetString("UserType");
            HttpContext.Session.Clear();

            if (userType == "Admin")
            {
                return RedirectToAction("AdminLogin", "Account", new { clear = "true" });
            }
            else
            {
                return RedirectToAction("UserLogin", "Account", new { clear = "true" });
            }
        }
    }
}
