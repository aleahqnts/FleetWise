using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    public class UsersController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
