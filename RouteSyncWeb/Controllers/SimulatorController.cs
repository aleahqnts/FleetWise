using FleetWise.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    // Operator control for the demo telemetry simulator. Toggled from the hidden Fleet Map
    // switch. OFF also wipes the data the simulator created (real trips are left intact).
    [Authorize]
    public class SimulatorController : Controller
    {
        private readonly SimulatorControl _control;
        public SimulatorController(SimulatorControl control) => _control = control;

        [HttpGet]
        public IActionResult Status() => Json(new { enabled = _control.Enabled });

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Start()
        {
            _control.Start();
            return Json(new { enabled = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Stop()
        {
            var removed = await _control.StopAndCleanupAsync();
            return Json(new { enabled = false, removed });
        }
    }
}
