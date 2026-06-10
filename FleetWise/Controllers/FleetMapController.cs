using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    public class FleetMapController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
