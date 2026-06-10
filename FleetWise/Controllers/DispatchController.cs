using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    public class DispatchController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
