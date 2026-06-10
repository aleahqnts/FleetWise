using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    public class VehiclesController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
