using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;

namespace FleetWise.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Index(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Hardcoded credentials for now
            if (model.Email == "admin@fleetwise.com" && model.Password == "admin123")
            {
                return RedirectToAction("Index", "Dashboard");
            }

            ModelState.AddModelError("", "Invalid email or password.");
            return View(model);
        }
    }
}
