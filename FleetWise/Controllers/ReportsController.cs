using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    public class ReportsController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
