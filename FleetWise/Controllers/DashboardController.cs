using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    public class DashboardController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
