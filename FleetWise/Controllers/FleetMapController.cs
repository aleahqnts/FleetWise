using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    [Authorize]
    public class FleetMapController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
