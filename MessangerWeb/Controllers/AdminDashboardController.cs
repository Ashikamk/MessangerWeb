using Microsoft.AspNetCore.Mvc;

namespace MessangerWeb.Controllers
{
    public class AdminDashboardController : Controller
    {
        public IActionResult Index()
        {
            // Check if user is admin
            var userType = HttpContext.Session.GetString("UserType");
            if (userType != "Admin")
            {
                return RedirectToAction("AdminLogin", "Account");
            }
            return View();
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("AdminLogin", "Account");
        }
    }
}
